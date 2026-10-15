import { useI18n } from 'vue-i18n'

/**
 * A card's tab title in the app's language.
 *
 * Layout snapshots keep a title per card, and the built-in ones used to be written in the
 * language the layout was created in, so a saved layout kept showing Chinese tab titles in the
 * English app. A title that is still its card type's own name — in any language, the bare type
 * id, or empty — is shown in the current language; a title the card was given (a page, a work
 * item, a command) is shown as stored.
 */
export function useCardTitle() {
  const { t, te, availableLocales } = useI18n()
  return (card: { descriptorId: string; title?: string | null }): string => {
    const key = `uie.cardTitle.${card.descriptorId}`
    const title = card.title?.trim() ?? ''
    if (!te(key)) return title || card.descriptorId
    const builtIn = !title || title === card.descriptorId
      || availableLocales.some((locale) => t(key, {}, { locale }) === title)
    return builtIn ? t(key) : title
  }
}
