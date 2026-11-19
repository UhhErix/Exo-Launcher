/**
 * The GitHub REST calls the Worker makes (README.md › Issues), all on one private repository. The token is the secret
 * GITHUB_REPORTS_TOKEN: a fine-grained token for that repository alone with Issues read and write. Without it there is no
 * client and the Worker only stores and counts. Nothing but issues, their comments and labels is touched.
 */

export const REPO = 'UhhErix/Exo-Reports'
const API = 'https://api.github.com'
const TIMEOUT_MS = 8000

export class GitHubError extends Error {
  constructor(status, what) {
    super(`GitHub answered ${status} to ${what}`)
    this.status = status
  }
}

/** A client for REPO, or null when the token is not set (the Worker then files nothing). `env.fetcher` replaces fetch in tests. */
export function gitHubOf(env) {
  const token = env.GITHUB_REPORTS_TOKEN
  if (typeof token !== 'string' || token.trim() === '') return null
  const post = env.fetcher ?? ((...args) => fetch(...args))

  async function call(method, path, body) {
    let response
    try {
      response = await post(`${API}/repos/${REPO}${path}`, {
        method,
        headers: {
          Accept: 'application/vnd.github+json',
          Authorization: `Bearer ${token.trim()}`,
          'User-Agent': 'exo-reports-worker',
          'X-GitHub-Api-Version': '2022-11-28',
          ...(body ? { 'Content-Type': 'application/json' } : {}),
        },
        body: body ? JSON.stringify(body) : undefined,
        signal: AbortSignal.timeout(TIMEOUT_MS),
      })
    } catch {
      throw new GitHubError(0, `${method} ${path.split('/')[1]}`)
    }
    if (!response.ok) throw new GitHubError(response.status, `${method} ${path.split('/')[1]}`)
    return response.status === 204 ? null : response.json()
  }

  return {
    createIssue: ({ title, body, labels }) => call('POST', '/issues', { title, body, labels }),
    getIssue: (number) => call('GET', `/issues/${number}`),
    updateIssue: (number, fields) => call('PATCH', `/issues/${number}`, fields),
    comment: (number, body) => call('POST', `/issues/${number}/comments`, { body }),
    addLabels: (number, labels) => call('POST', `/issues/${number}/labels`, { labels }),
    /** Makes a label; one that already exists (422) is fine. */
    async ensureLabel(name, color = 'ededed') {
      try {
        await call('POST', '/labels', { name, color })
      } catch (error) {
        if (!(error instanceof GitHubError && error.status === 422)) throw error
      }
    },
  }
}
