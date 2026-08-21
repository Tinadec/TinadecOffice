export type DispatchPref = 'queued' | 'parallel' | 'ask'

export const ENTER_PREF_KEY = 'tinadec.enter_pref'
export const MODE_VERSION_PREF_KEY = 'tinadec.mode_version_pref'
export const MEETING_MODEL_PREF_KEY = 'tinadec.meeting_model_pref'

export function getDispatchPref(): DispatchPref {
  const v = localStorage.getItem(ENTER_PREF_KEY)
  return v === 'parallel' || v === 'ask' ? v : 'queued'
}

export function setDispatchPref(v: DispatchPref): void {
  localStorage.setItem(ENTER_PREF_KEY, v)
}

export function getModeVersionPref(): string | null {
  return localStorage.getItem(MODE_VERSION_PREF_KEY) || null
}

export function setModeVersionPref(v: string | null): void {
  if (v) {
    localStorage.setItem(MODE_VERSION_PREF_KEY, v)
  } else {
    localStorage.removeItem(MODE_VERSION_PREF_KEY)
  }
}

export function getMeetingModelPref(): string {
  return localStorage.getItem(MEETING_MODEL_PREF_KEY) || ''
}

export function setMeetingModelPref(v: string): void {
  const trimmed = v.trim()
  if (trimmed) {
    localStorage.setItem(MEETING_MODEL_PREF_KEY, trimmed)
  } else {
    localStorage.removeItem(MEETING_MODEL_PREF_KEY)
  }
}

