import { describe, expect, it } from 'vitest'
import { postLoginTarget, shouldShowShell } from '../navigation'

describe('shouldShowShell (BUG-4 regression)', () => {
  it('keeps the shell on a guarded page right after logout cleared the session', () => {
    // logout clears auth while the dashboard is still the current route: no layout flip/remount
    expect(shouldShowShell({ policy: 'operator' } as never, false)).toBe(true)
  })

  it('renders bare for login and for anonymous public pages', () => {
    expect(shouldShowShell({ public: true, bare: true }, false)).toBe(false)
    expect(shouldShowShell({ public: true, bare: true }, true)).toBe(false)
    expect(shouldShowShell({ public: true }, false)).toBe(false)
  })

  it('renders the shell for authenticated users on normal and public pages', () => {
    expect(shouldShowShell({}, true)).toBe(true)
    expect(shouldShowShell({ public: true }, true)).toBe(true)
  })
})

describe('postLoginTarget', () => {
  it('accepts only safe local redirects', () => {
    expect(postLoginTarget('/stocks?q=EL', '/')).toBe('/stocks?q=EL')
    expect(postLoginTarget('//evil.com', '/')).toBe('/')
    expect(postLoginTarget('/login', '/my-account')).toBe('/my-account')
    expect(postLoginTarget(undefined, '/')).toBe('/')
  })
})
