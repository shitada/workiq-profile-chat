const consentHostSuffixes = [
  '.consent.azure-apim.net',
  '.consent.azure-apihub.net',
]

export function isAllowedConsentUrl(value: string): boolean {
  try {
    const url = new URL(value)
    return (
      url.protocol === 'https:' &&
      url.pathname === '/login' &&
      consentHostSuffixes.some((suffix) => url.hostname.endsWith(suffix))
    )
  } catch {
    return false
  }
}
