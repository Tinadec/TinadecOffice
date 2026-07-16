import { nextTick, ref } from 'vue'

const focusableSelector = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'

export function useDialogFocus(close: () => void) {
  const dialogRef = ref<HTMLElement | null>(null)
  let returnFocus: HTMLElement | null = null

  function openDialog() {
    returnFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null
    void nextTick(() => {
      const dialog = dialogRef.value
      ;(dialog?.querySelector<HTMLElement>(focusableSelector) ?? dialog)?.focus()
    })
  }

  function closeDialog() {
    close()
    void nextTick(() => returnFocus?.focus())
  }

  function onDialogKeydown(event: KeyboardEvent) {
    if (event.key === 'Escape') {
      event.preventDefault()
      closeDialog()
      return
    }
    if (event.key !== 'Tab' || !dialogRef.value) return

    const focusable = Array.from(dialogRef.value.querySelectorAll<HTMLElement>(focusableSelector))
    if (focusable.length === 0) {
      event.preventDefault()
      dialogRef.value.focus()
      return
    }

    const first = focusable[0]
    const last = focusable[focusable.length - 1]
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault()
      last.focus()
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault()
      first.focus()
    }
  }

  return { closeDialog, dialogRef, onDialogKeydown, openDialog }
}
