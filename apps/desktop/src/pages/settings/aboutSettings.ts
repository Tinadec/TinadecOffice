import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { api } from '@/api'

const aboutGatewayStatus = ref('')

async function checkAboutHealth() {
  try {
    const response = await fetch(`${api.gatewayUrl}/api/v1/health`)
    const data = await response.json()
    aboutGatewayStatus.value = data.status === 'ok' ? 'ok' : ''
  } catch {
    aboutGatewayStatus.value = ''
  }
}

export function useAboutSettings() {
  const { t } = useI18n()
  if (!aboutGatewayStatus.value) void checkAboutHealth()

  function openExternal(url: string) {
    window.open(url, '_blank')
  }

  return { aboutGatewayStatus, openExternal, t }
}
