export type DispatchPref = 'queued' | 'parallel' | 'ask'

export const ENTER_PREF_KEY = 'tinadec.enter_pref'

export function getDispatchPref(): DispatchPref {
  const v = localStorage.getItem(ENTER_PREF_KEY)
  return v === 'parallel' || v === 'ask' ? v : 'queued'
}

export function setDispatchPref(v: DispatchPref): void {
  localStorage.setItem(ENTER_PREF_KEY, v)
}
