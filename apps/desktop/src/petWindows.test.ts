import { describe, expect, it } from 'vitest'
import app from './App.vue?raw'
import petStore from '../electron/petStore.cjs?raw'
import mainProcess from '../electron/main.cjs?raw'
import preload from '../electron/preload.cjs?raw'
import desktopPetPage from './pages/DesktopPetPage.vue?raw'
import router from './router.ts?raw'

describe('local pet window shell', () => {

  it('keeps Petdex download and local pet actions inside the desktop process', () => {
    expect(preload).toContain("ipcRenderer.invoke('tinadec:pet-create', petId)")
    expect(preload).toContain("ipcRenderer.invoke('tinadec:pet-close', instanceId)")
    expect(preload).toContain("ipcRenderer.invoke('tinadec:pet-download', slug)")
    expect(preload).toContain("ipcRenderer.invoke('tinadec:pet-open-folder', slug)")
    expect(preload).toContain("ipcRenderer.invoke('tinadec:pet-remove', slug)")
    expect(petStore).toContain("const PETDEX_MANIFEST_URL = 'https://petdex.dev/api/manifest/v2'")
    expect(petStore).toContain('manifest.v !== 2')
    expect(petStore).toContain("app.getPath('userData')")
    expect(petStore).toContain('Petdex asset URL is not trusted')
    expect(petStore).toContain("Referer: 'https://petdex.dev/'")
    expect(petStore).toContain('const previewRequests = new Map()')
    expect(petStore).toContain('MAX_PREVIEW_CACHE_BYTES')
    expect(mainProcess).toContain("scheme: 'tinadec-pet-preview'")
    expect(mainProcess).toContain("protocol.handle('tinadec-pet-preview'")
    expect(petStore).toContain('await fs.rename(temporary, destination)')
    expect(router).toContain("path: '/pet'")
    expect(app).toContain("const isPetWindow = window.location.hash.startsWith('#/pet')")
    // Child windows skip the splash but still observe their own host authorization.
    expect(app).toContain('if (!isPetWindow) void startConnection()')
    expect(app).toContain('const entryReady = ref(isChildWindow)')
    expect(desktopPetPage).toContain('background: transparent !important')
    expect(desktopPetPage).toContain("from '@/pets/petRuntime'")
    expect(desktopPetPage).toContain('requestAnimationFrame(draw)')
    expect(desktopPetPage).toContain('event.screenX')
    expect(desktopPetPage).toContain('getImageData')
    expect(desktopPetPage).toContain('Math.min(1.2, Math.max(0.18, next))')
    expect(desktopPetPage).toContain("setState('running-right')")
    expect(petStore).not.toContain('normalizePetDefinition')
    expect(preload).toContain("ipcRenderer.on('tinadec:pet-changed', handler)")
  })

})
