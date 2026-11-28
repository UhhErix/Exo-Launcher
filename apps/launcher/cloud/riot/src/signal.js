export const SAMPLE_EVERY = { '2xx': 100, '3xx': 100, '4xx': 5 }

export const classOf = (status) => (Number.isInteger(status) && status >= 100 && status < 600 ? `${Math.floor(status / 100)}xx` : 'other')

export function routeIn(table) {
  return (pathname) => table.find(([, pattern]) => pattern.test(pathname))?.[0] ?? 'other'
}

export function write(env, worker, route, klass, weight = 1) {
  try {
    env.SIGNAL?.writeDataPoint({ blobs: [worker, route, klass], doubles: [weight], indexes: [worker] })
  } catch {}
}

export function record(env, worker, route, status) {
  const klass = classOf(status)
  const weight = SAMPLE_EVERY[klass] ?? 1
  if (weight > 1 && Math.random() * weight >= 1) return
  write(env, worker, route, klass, weight)
}

export function counted(worker, routes, handler) {
  const routeOf = routeIn(routes)
  return {
    ...handler,
    async fetch(request, env, ctx) {
      let status = 500
      try {
        const response = await handler.fetch(request, env, ctx)
        status = response.status
        return response
      } finally {
        record(env, worker, routeOf(new URL(request.url).pathname), status)
      }
    },
  }
}
