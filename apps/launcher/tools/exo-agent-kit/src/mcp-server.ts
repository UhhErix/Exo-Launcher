import { StdioMcpServer, type McpToolDefinition } from './protocol/mcp.js'
import { runApplyEdits } from './tools/apply-edits.js'
import { runBatchSearch } from './tools/batch-search.js'
import { runExoContract } from './tools/exo-contract.js'
import { runExoParitySnap, VALID_PARITY_SCENES } from './tools/exo-parity-snap.js'
import { runExoGates } from './tools/exo-run-gates.js'
import { runExoRuneApi } from './tools/exo-rune-api.js'
import { runExoSnapshot } from './tools/exo-snapshot.js'
import { runQuickOutline } from './tools/quick-outline.js'

const TOOLS: McpToolDefinition[] = [
  {
    name: 'exo_run_gates',
    description:
      'Run Exo Launcher verification gates mapped from changed files or explicit paths/gates concurrently with compact <=8.8 KB folded output.',
    inputSchema: {
      type: 'object',
      properties: {
        cwd: { type: 'string', description: 'Worktree or subdirectory path' },
        paths: {
          type: 'array',
          items: { type: 'string' },
          description: 'Changed file paths to map to gates (defaults to git status)',
        },
        gates: {
          type: 'array',
          items: { type: 'string' },
          description: 'Explicit gate IDs (e.g. cargo-fmt, ui-check, host-test, comments)',
        },
        concurrency: { type: 'number', description: 'Worker pool size (1..8, default 4)' },
        timeoutSec: { type: 'number', description: 'Per-call timeout seconds (max 165)' },
        dryRun: { type: 'boolean', description: 'List mapped gates without running them' },
        maxBytes: { type: 'number', description: 'Output cap in JSON bytes (<=8800)' },
      },
      additionalProperties: false,
    },
    handler: runExoGates,
  },
  {
    name: 'batch_search',
    description:
      'Run 1..20 concurrent text, regex, glob, fuzzy, or read-only git queries with direct-spawn rg and water-fill <=8.8 KB budgeting.',
    inputSchema: {
      type: 'object',
      required: ['queries'],
      properties: {
        cwd: { type: 'string', description: 'Base directory for relative paths' },
        queries: {
          type: 'array',
          items: {
            type: 'object',
            required: ['type', 'query'],
            properties: {
              id: { type: 'string' },
              type: { type: 'string', enum: ['text', 'regex', 'glob', 'fuzzy', 'git'] },
              query: { type: 'string' },
              path: { type: 'string' },
              glob: { type: 'string' },
              caseSensitive: { type: 'boolean' },
              maxResults: { type: 'number' },
              args: { type: 'array', items: { type: 'string' } },
            },
          },
        },
        maxBytes: { type: 'number', description: 'Output cap in JSON bytes (<=8800)' },
      },
      additionalProperties: false,
    },
    handler: runBatchSearch,
  },
  {
    name: 'exo_contract',
    description:
      'Lookup Exo host-v1.json RPC methods/events across TS host.ts, C# WebHostBridge*.cs dispatcher/handlers, and Rust exo-rpc.',
    inputSchema: {
      type: 'object',
      properties: {
        cwd: { type: 'string' },
        method: { type: 'string', description: 'Exact RPC method name (e.g. game.get)' },
        event: { type: 'string', description: 'Exact host event name' },
        prefix: { type: 'string', description: 'Filter methods/events by prefix' },
        query: { type: 'string', description: 'Substring search across names and types' },
        limit: { type: 'number', description: 'Max listed items in search mode' },
        maxBytes: { type: 'number' },
      },
      additionalProperties: false,
    },
    handler: runExoContract,
  },
  {
    name: 'exo_rune_api',
    description:
      'Search pinned Rune checkout public API (pub [const|async|unsafe] fn, struct, enum, trait, impl) using offline rev-keyed cache.',
    inputSchema: {
      type: 'object',
      properties: {
        cwd: { type: 'string' },
        query: { type: 'string', description: 'Symbol name, substring, or signature fragment' },
        owner: { type: 'string', description: 'Filter by enclosing impl/type owner' },
        kind: {
          type: 'string',
          enum: ['all', 'fn', 'struct', 'enum', 'trait', 'type', 'const', 'impl'],
        },
        crate: { type: 'string', description: 'Filter by Rune crate name (e.g. rune-core)' },
        limit: { type: 'number' },
        refresh: { type: 'boolean', description: 'Rebuild offline index cache for pinned rev' },
        maxBytes: { type: 'number' },
      },
      additionalProperties: false,
    },
    handler: runExoRuneApi,
  },
  {
    name: 'quick_outline',
    description:
      'Outline 1..20 C#, Rust, or TS/JS files with exact [startLine..endLine] ranges at any brace depth to feed ranged reads.',
    inputSchema: {
      type: 'object',
      required: ['paths'],
      properties: {
        cwd: { type: 'string' },
        paths: { type: 'array', items: { type: 'string' } },
        filter: { type: 'string', description: 'Optional regex filter on symbol name/signature' },
        maxSymbolsPerFile: { type: 'number' },
        maxBytes: { type: 'number' },
      },
      additionalProperties: false,
    },
    handler: runQuickOutline,
  },
  {
    name: 'exo_snapshot',
    description:
      'Compact read-only workspace snapshot: active branch, Orca --base-branch guard, dirty files, last 3 commits, active areas, and required Exo skills.',
    inputSchema: {
      type: 'object',
      properties: {
        cwd: { type: 'string' },
        maxBytes: { type: 'number' },
      },
      additionalProperties: false,
    },
    handler: runExoSnapshot,
  },
  {
    name: 'exo_parity_snap',
    description:
      'Wrap in-repo tools/parity/parity-snap.ps1 and parity-compare.py for named native UI scenes or direct PNG diff scoring.',
    inputSchema: {
      type: 'object',
      properties: {
        cwd: { type: 'string' },
        worktreeRoot: { type: 'string' },
        scenes: {
          type: 'array',
          items: { type: 'string', enum: [...VALID_PARITY_SCENES] },
        },
        outDir: { type: 'string' },
        tag: { type: 'string' },
        noBuild: { type: 'boolean' },
        refsDir: { type: 'string' },
        hostExe: { type: 'string' },
        dataDir: { type: 'string' },
        dryRun: { type: 'boolean' },
        compare: {
          type: 'object',
          properties: {
            refPath: { type: 'string' },
            snapPath: { type: 'string' },
            outPath: { type: 'string' },
          },
        },
        timeoutSec: { type: 'number' },
        maxBytes: { type: 'number' },
      },
      additionalProperties: false,
    },
    handler: runExoParitySnap,
  },
  {
    name: 'apply_edits',
    description:
      'Apply atomic or bestEffort multi-file edits (1..50 files), preserving CRLF/mode, with closest-line hints and optional direct-spawn verify.',
    inputSchema: {
      type: 'object',
      required: ['edits'],
      properties: {
        cwd: { type: 'string' },
        edits: {
          type: 'array',
          items: {
            type: 'object',
            required: ['path'],
            properties: {
              path: { type: 'string' },
              hunks: {
                type: 'array',
                items: {
                  type: 'object',
                  required: ['oldText', 'newText'],
                  properties: {
                    oldText: { type: 'string' },
                    newText: { type: 'string' },
                    replaceAll: { type: 'boolean' },
                  },
                },
              },
              oldText: { type: 'string' },
              newText: { type: 'string' },
              replaceAll: { type: 'boolean' },
              createIfMissing: { type: 'boolean' },
              content: { type: 'string' },
            },
          },
        },
        bestEffort: { type: 'boolean' },
        dryRun: { type: 'boolean' },
        verify: {
          type: 'object',
          properties: {
            command: { type: 'string' },
            args: { type: 'array', items: { type: 'string' } },
            cwd: { type: 'string' },
            timeoutSec: { type: 'number' },
          },
        },
        maxBytes: { type: 'number' },
      },
      additionalProperties: false,
    },
    handler: runApplyEdits,
  },
]

const server = new StdioMcpServer('exo-agent-kit', '1.0.0', TOOLS)
server.start()
