// API tokens for scripts. Minting and revoking need the browser session (the server refuses a
// token-authenticated caller), which is why this lives in the SPA at all. Mirrors ApiTokenService.

export interface ApiTokenSummary {
  id: string
  name: string
  devScope: boolean
  createdAt: string
  expiresAt: string | null
  revokedAt: string | null
  active: boolean
}

// The only time the secret exists outside the caller's hands — the server stores a hash of it.
export interface ApiTokenMinted {
  id: string
  token: string
  name: string
  subject: string
  devScope: boolean
  expiresAt: string | null
}

// Mint refusals come back two ways: `{ error }` for a bad request, a ProblemDetails `detail` for the
// dev-scope 403. Either carries the sentence worth showing.
async function errorText(res: Response, fallback: string): Promise<string> {
  try {
    const body = (await res.json()) as { error?: string; detail?: string }
    if (body.error) return body.error
    if (body.detail) return body.detail
  } catch {
    // Not JSON — fall through to the status line.
  }
  return `${fallback}: ${res.status} ${res.statusText}`
}

export async function getApiTokens(): Promise<ApiTokenSummary[]> {
  const res = await fetch('/api/tokens')
  if (!res.ok) {
    throw new Error(await errorText(res, 'Failed to load tokens'))
  }
  return (await res.json()) as ApiTokenSummary[]
}

// expiresInDays null = never expires.
export async function mintApiToken(
  name: string,
  expiresInDays: number | null,
  dev: boolean,
): Promise<ApiTokenMinted> {
  const res = await fetch('/api/tokens', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name, expiresInDays, dev }),
  })
  if (!res.ok) {
    throw new Error(await errorText(res, 'Failed to mint the token'))
  }
  return (await res.json()) as ApiTokenMinted
}

export async function revokeApiToken(id: string): Promise<void> {
  const res = await fetch(`/api/tokens/${encodeURIComponent(id)}`, { method: 'DELETE' })
  if (!res.ok) {
    throw new Error(await errorText(res, 'Failed to revoke the token'))
  }
}
