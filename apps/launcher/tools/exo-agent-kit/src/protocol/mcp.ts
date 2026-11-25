import { clampByteBudget, DEFAULT_INLINE_MAX_BYTES, truncateToJsonBytes } from '../utils/budget.js'

export interface JsonSchema {
  type: string
  description?: string
  properties?: Record<string, unknown>
  required?: string[]
  items?: unknown
  enum?: string[]
  additionalProperties?: boolean
}

export interface ToolResult {
  text: string
  isError?: boolean
}

export interface McpToolDefinition {
  name: string
  description: string
  inputSchema: JsonSchema
  handler: (args: Record<string, unknown>) => Promise<ToolResult>
}

interface JsonRpcRequest {
  jsonrpc?: string
  id?: string | number | null
  method?: string
  params?: Record<string, unknown>
}

type FramingMode = 'ndjson' | 'content-length'

const SUPPORTED_PROTOCOLS = new Set(['2025-06-18', '2025-03-26', '2024-11-05'])

export class StdioMcpServer {
  private readonly serverName: string
  private readonly serverVersion: string
  private readonly tools: McpToolDefinition[]
  private readonly toolMap: Map<string, McpToolDefinition>
  private framing: FramingMode = 'ndjson'
  private buffer: Buffer = Buffer.alloc(0)
  private processing = false
  private readonly queue: string[] = []

  constructor(name: string, version: string, tools: McpToolDefinition[]) {
    this.serverName = name
    this.serverVersion = version
    this.tools = tools
    this.toolMap = new Map(tools.map((t) => [t.name, t]))
  }

  start(): void {
    process.stdin.on('data', (chunk: Buffer) => {
      this.buffer = Buffer.concat([this.buffer, chunk])
      this.extractMessages()
      void this.drainQueue()
    })
    process.stdin.on('end', () => {
      if (this.buffer.length > 0) {
        const trailing = this.buffer.toString('utf8').trim()
        this.buffer = Buffer.alloc(0)
        if (trailing) {
          this.queue.push(trailing)
          void this.drainQueue()
        }
      }
    })
    process.stdin.resume()
  }

  private extractMessages(): void {
    while (this.buffer.length > 0) {
      let start = 0
      while (
        start < this.buffer.length &&
        (this.buffer[start] === 0x20 ||
          this.buffer[start] === 0x09 ||
          this.buffer[start] === 0x0d ||
          this.buffer[start] === 0x0a)
      ) {
        start++
      }
      if (start > 0) {
        this.buffer = this.buffer.subarray(start)
      }
      if (this.buffer.length === 0) return

      const head = this.buffer.subarray(0, Math.min(32, this.buffer.length)).toString('ascii')
      if (/^Content-Length:/i.test(head)) {
        this.framing = 'content-length'
        const sepIdx = this.buffer.indexOf('\r\n\r\n')
        const altSepIdx = sepIdx >= 0 ? sepIdx : this.buffer.indexOf('\n\n')
        const sepLen = sepIdx >= 0 ? 4 : 2
        if (altSepIdx < 0) return
        const headerText = this.buffer.subarray(0, altSepIdx).toString('ascii')
        const match = /Content-Length:\s*(\d+)/i.exec(headerText)
        if (!match) {
          this.buffer = this.buffer.subarray(altSepIdx + sepLen)
          continue
        }
        const contentLength = Number(match[1])
        const bodyStart = altSepIdx + sepLen
        if (this.buffer.length < bodyStart + contentLength) return
        const body = this.buffer.subarray(bodyStart, bodyStart + contentLength).toString('utf8')
        this.buffer = this.buffer.subarray(bodyStart + contentLength)
        this.queue.push(body)
      } else {
        const nlIdx = this.buffer.indexOf(0x0a)
        if (nlIdx < 0) return
        const line = this.buffer.subarray(0, nlIdx).toString('utf8').trim()
        this.buffer = this.buffer.subarray(nlIdx + 1)
        if (line) this.queue.push(line)
      }
    }
  }

  private async drainQueue(): Promise<void> {
    if (this.processing) return
    this.processing = true
    try {
      while (this.queue.length > 0) {
        const raw = this.queue.shift()!
        await this.handleRawMessage(raw)
      }
    } finally {
      this.processing = false
    }
  }

  private writeMessage(payload: Record<string, unknown>): void {
    const json = JSON.stringify(payload)
    if (this.framing === 'content-length') {
      const len = Buffer.byteLength(json, 'utf8')
      process.stdout.write(`Content-Length: ${len}\r\n\r\n${json}`)
    } else {
      process.stdout.write(`${json}\n`)
    }
  }

  private writeResult(id: string | number | null, result: Record<string, unknown>): void {
    this.writeMessage({ jsonrpc: '2.0', id, result })
  }

  private writeError(
    id: string | number | null,
    code: number,
    message: string,
    data?: unknown,
  ): void {
    const error: Record<string, unknown> = { code, message }
    if (data !== undefined) error.data = data
    this.writeMessage({ jsonrpc: '2.0', id, error })
  }

  private async handleRawMessage(raw: string): Promise<void> {
    let req: JsonRpcRequest
    try {
      req = JSON.parse(raw) as JsonRpcRequest
    } catch (err) {
      this.writeError(null, -32700, `Parse error: ${err instanceof Error ? err.message : String(err)}`)
      return
    }

    if (!req || typeof req !== 'object' || typeof req.method !== 'string') {
      if (req && req.id !== undefined) {
        this.writeError(req.id ?? null, -32600, 'Invalid Request')
      }
      return
    }

    const isNotification = req.id === undefined
    const id = req.id ?? null

    try {
      switch (req.method) {
        case 'initialize': {
          const clientVersion =
            typeof req.params?.protocolVersion === 'string' ? req.params.protocolVersion : ''
          const protocolVersion = SUPPORTED_PROTOCOLS.has(clientVersion)
            ? clientVersion
            : '2025-03-26'
          if (!isNotification) {
            this.writeResult(id, {
              protocolVersion,
              capabilities: { tools: {} },
              serverInfo: {
                name: this.serverName,
                version: this.serverVersion,
              },
            })
          }
          return
        }

        case 'notifications/initialized':
        case 'notifications/cancelled':
          return

        case 'ping': {
          if (!isNotification) this.writeResult(id, {})
          return
        }

        case 'tools/list': {
          if (!isNotification) {
            this.writeResult(id, {
              tools: this.tools.map((t) => ({
                name: t.name,
                description: t.description,
                inputSchema: t.inputSchema,
              })),
            })
          }
          return
        }

        case 'tools/call': {
          const params = req.params ?? {}
          const toolName = typeof params.name === 'string' ? params.name : ''
          const toolArgs =
            params.arguments && typeof params.arguments === 'object' && !Array.isArray(params.arguments)
              ? (params.arguments as Record<string, unknown>)
              : {}
          const tool = this.toolMap.get(toolName)
          if (!tool) {
            if (!isNotification) {
              this.writeError(id, -32602, `Unknown tool: ${toolName}`)
            }
            return
          }

          const requestedCap =
            typeof toolArgs.maxBytes === 'number' ? toolArgs.maxBytes : DEFAULT_INLINE_MAX_BYTES
          const hardCap = clampByteBudget(requestedCap, DEFAULT_INLINE_MAX_BYTES)

          let outcome: ToolResult
          try {
            outcome = await tool.handler(toolArgs)
          } catch (err) {
            outcome = {
              text: `Tool "${toolName}" error: ${err instanceof Error ? err.message : String(err)}`,
              isError: true,
            }
          }

          const cappedText = truncateToJsonBytes(outcome.text, hardCap)
          if (!isNotification) {
            this.writeResult(id, {
              content: [{ type: 'text', text: cappedText }],
              ...(outcome.isError ? { isError: true } : {}),
            })
          }
          return
        }

        default: {
          if (!isNotification) {
            this.writeError(id, -32601, `Method not found: ${req.method}`)
          }
        }
      }
    } catch (err) {
      if (!isNotification) {
        this.writeError(id, -32603, err instanceof Error ? err.message : String(err))
      }
    }
  }
}
