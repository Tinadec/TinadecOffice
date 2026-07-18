const { app } = require('electron');
const fs = require('node:fs/promises');
const path = require('node:path');

const PETDEX_MANIFEST_URL = 'https://petdex.dev/api/manifest/v2';
const MANIFEST_CACHE_MS = 5 * 60 * 1000;
const FETCH_TIMEOUT_MS = 20_000;
const MAX_MANIFEST_BYTES = 8 * 1024 * 1024;
const MAX_PET_JSON_BYTES = 512 * 1024;
const MAX_SPRITESHEET_BYTES = 10 * 1024 * 1024;
const MAX_PREVIEW_CACHE_BYTES = 48 * 1024 * 1024;
const DEFAULT_PET_SCALE = 220 / 192;
let manifestCache = null;
let previewCacheBytes = 0;
const previewCache = new Map();
const previewRequests = new Map();

function petsRoot() {
  return path.join(app.getPath('userData'), 'pets');
}

function registryPath() {
  return path.join(petsRoot(), 'registry.json');
}

function safeSlug(value) {
  const slug = String(value || '').trim().toLowerCase().replace(/[^a-z0-9._-]+/g, '-').replace(/^-+|-+$/g, '');
  if (!slug || slug.length > 120) throw new Error('Invalid pet id');
  return slug;
}

function petDirectory(slug) {
  return path.join(petsRoot(), safeSlug(slug));
}

function trustedPetdexUrl(value) {
  const url = new URL(value);
  const host = url.hostname.toLowerCase();
  if (url.protocol !== 'https:' || url.port || url.username || url.password || (host !== 'petdex.dev' && !host.endsWith('.petdex.dev'))) {
    throw new Error('Petdex asset URL is not trusted');
  }
  return url;
}

async function fetchTrusted(urlValue, maxBytes) {
  let url = trustedPetdexUrl(urlValue);
  for (let redirects = 0; redirects < 4; redirects += 1) {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), FETCH_TIMEOUT_MS);
    try {
      const response = await fetch(url, {
        redirect: 'manual',
        headers: {
          'User-Agent': 'TinadecOffice-Pets/0.1',
          Referer: 'https://petdex.dev/',
        },
        signal: controller.signal,
      });
      if (response.status >= 300 && response.status < 400) {
        const location = response.headers.get('location');
        if (!location) throw new Error('Petdex redirect has no location');
        url = trustedPetdexUrl(new URL(location, url).toString());
        continue;
      }
      if (!response.ok || !response.body) throw new Error(`Petdex download failed: ${response.status}`);
      const advertisedLength = Number(response.headers.get('content-length') || '0');
      if (Number.isFinite(advertisedLength) && advertisedLength > maxBytes) throw new Error('Petdex asset is too large');
      const reader = response.body.getReader();
      const chunks = [];
      let size = 0;
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        size += value.byteLength;
        if (size > maxBytes) {
          await reader.cancel();
          throw new Error('Petdex asset is too large');
        }
        chunks.push(Buffer.from(value));
      }
      return { buffer: Buffer.concat(chunks), contentType: response.headers.get('content-type') || '', url };
    } finally {
      clearTimeout(timeout);
    }
  }
  throw new Error('Petdex redirect limit exceeded');
}

function asString(value) {
  return typeof value === 'string' ? value.trim() : '';
}

function normalizeCatalogPet(value, fields, assetBase) {
  if (!Array.isArray(value)) return null;
  const item = Object.fromEntries(fields.map((field, index) => [field, value[index]]));
  const slug = safeSlug(asString(item.slug));
  const spritesheet = asString(item.spritesheet);
  const petJson = asString(item.petJson);
  if (!spritesheet || !petJson) return null;
  const spritesheetUrl = new URL(spritesheet, assetBase).toString();
  const petJsonUrl = new URL(petJson, assetBase).toString();
  trustedPetdexUrl(spritesheetUrl);
  trustedPetdexUrl(petJsonUrl);
  return {
    slug,
    displayName: asString(item.displayName) || slug,
    kind: asString(item.kind) || 'pet',
    submittedBy: asString(item.submittedBy),
    spritesheetUrl,
    petJsonUrl,
    zipUrl: item.zip ? new URL(asString(item.zip), assetBase).toString() : '',
  };
}

async function fetchCatalog(force = false) {
  if (!force && manifestCache && manifestCache.expiresAt > Date.now()) return manifestCache.pets;
  const { buffer } = await fetchTrusted(PETDEX_MANIFEST_URL, MAX_MANIFEST_BYTES);
  const manifest = JSON.parse(buffer.toString('utf8'));
  if (!manifest || manifest.v !== 2 || !Array.isArray(manifest.fields) || !Array.isArray(manifest.pets)) {
    throw new Error('Petdex manifest is invalid');
  }
  const assetBase = trustedPetdexUrl(asString(manifest.assetBase)).toString();
  const fields = manifest.fields.map(asString);
  const pets = manifest.pets.map((item) => {
    try { return normalizeCatalogPet(item, fields, assetBase); } catch { return null; }
  }).filter(Boolean);
  manifestCache = { expiresAt: Date.now() + MANIFEST_CACHE_MS, pets };
  return pets;
}

function cachedPreview(slug) {
  const cached = previewCache.get(slug);
  if (!cached) return null;
  previewCache.delete(slug);
  previewCache.set(slug, cached);
  return cached;
}

function cachePreview(slug, preview) {
  if (preview.buffer.byteLength > MAX_PREVIEW_CACHE_BYTES) return;
  while (previewCacheBytes + preview.buffer.byteLength > MAX_PREVIEW_CACHE_BYTES && previewCache.size > 0) {
    const oldestSlug = previewCache.keys().next().value;
    const oldest = previewCache.get(oldestSlug);
    previewCache.delete(oldestSlug);
    previewCacheBytes -= oldest.buffer.byteLength;
  }
  previewCache.set(slug, preview);
  previewCacheBytes += preview.buffer.byteLength;
}

async function fetchPetPreview(slugInput) {
  const slug = safeSlug(slugInput);
  const cached = cachedPreview(slug);
  if (cached) return cached;
  if (previewRequests.has(slug)) return previewRequests.get(slug);

  const request = (async () => {
    const catalog = await fetchCatalog();
    const pet = catalog.find((item) => item.slug === slug);
    if (!pet) throw new Error('Pet was not found in the Petdex catalog');
    const response = await fetchTrusted(pet.spritesheetUrl, MAX_SPRITESHEET_BYTES);
    const extension = spriteExtension(pet.spritesheetUrl);
    if (!validSpritesheet(response.buffer, extension)) throw new Error('Pet preview format is invalid');
    const preview = {
      buffer: response.buffer,
      mime: extension === 'png' ? 'image/png' : 'image/webp',
    };
    cachePreview(slug, preview);
    return preview;
  })().finally(() => previewRequests.delete(slug));

  previewRequests.set(slug, request);
  return request;
}

async function readRegistry() {
  try {
    const value = JSON.parse(await fs.readFile(registryPath(), 'utf8'));
    return Array.isArray(value.pets) ? value : { pets: [] };
  } catch (error) {
    if (error && error.code === 'ENOENT') return { pets: [] };
    throw new Error('Local pet registry is unreadable');
  }
}

async function writeRegistry(registry) {
  await fs.mkdir(petsRoot(), { recursive: true });
  const temporaryPath = `${registryPath()}.tmp`;
  await fs.writeFile(temporaryPath, `${JSON.stringify(registry, null, 2)}\n`, 'utf8');
  await fs.rename(temporaryPath, registryPath());
}

function publicRecord(record) {
  return {
    slug: record.slug,
    displayName: record.displayName,
    kind: record.kind,
    submittedBy: record.submittedBy,
    enabled: Boolean(record.enabled),
    installedAt: record.installedAt,
    scale: typeof record.scale === 'number' ? record.scale : DEFAULT_PET_SCALE,
    window: record.window,
  };
}

async function listDownloaded() {
  const registry = await readRegistry();
  const pets = await Promise.all(registry.pets.map(async (record) => {
    try {
      const image = await fs.readFile(path.join(petDirectory(record.slug), record.spriteFile));
      const mime = record.spriteFile.endsWith('.png') ? 'image/png' : 'image/webp';
      return { ...publicRecord(record), imageDataUrl: `data:${mime};base64,${image.toString('base64')}` };
    } catch {
      return publicRecord(record);
    }
  }));
  return pets.sort((a, b) => Number(b.enabled) - Number(a.enabled) || a.displayName.localeCompare(b.displayName));
}

function spriteExtension(url) {
  return new URL(url).pathname.toLowerCase().endsWith('.png') ? 'png' : 'webp';
}

function validSpritesheet(buffer, extension) {
  if (extension === 'png') return buffer.length >= 8 && buffer.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]));
  return buffer.length >= 12 && buffer.subarray(0, 4).toString('ascii') === 'RIFF' && buffer.subarray(8, 12).toString('ascii') === 'WEBP';
}

async function downloadPet(slugInput) {
  const slug = safeSlug(slugInput);
  const registry = await readRegistry();
  const installed = registry.pets.find((pet) => pet.slug === slug);
  if (installed) return publicRecord(installed);
  const catalog = await fetchCatalog();
  const pet = catalog.find((item) => item.slug === slug);
  if (!pet) throw new Error('Pet was not found in the Petdex catalog');

  const [petJsonResponse, spritesheetResponse] = await Promise.all([
    fetchTrusted(pet.petJsonUrl, MAX_PET_JSON_BYTES),
    fetchTrusted(pet.spritesheetUrl, MAX_SPRITESHEET_BYTES),
  ]);
  const petJson = JSON.parse(petJsonResponse.buffer.toString('utf8'));
  if (!petJson || typeof petJson !== 'object') throw new Error('Pet metadata is invalid');
  const extension = spriteExtension(pet.spritesheetUrl);
  if (!validSpritesheet(spritesheetResponse.buffer, extension)) throw new Error('Pet spritesheet format is invalid');

  const destination = petDirectory(slug);
  const temporary = `${destination}.downloading`;
  await fs.rm(temporary, { recursive: true, force: true });
  try {
    await fs.mkdir(temporary, { recursive: true });
    const spriteFile = `spritesheet.${extension}`;
    await fs.writeFile(path.join(temporary, spriteFile), spritesheetResponse.buffer);
    await fs.writeFile(path.join(temporary, 'pet.json'), `${JSON.stringify({ ...petJson, id: slug, spritesheetPath: spriteFile }, null, 2)}\n`, 'utf8');
    const record = {
      slug,
      displayName: pet.displayName,
      kind: pet.kind,
      submittedBy: pet.submittedBy,
      enabled: false,
      spriteFile,
      source: 'petdex',
      spritesheetUrl: pet.spritesheetUrl,
      petJsonUrl: pet.petJsonUrl,
      installedAt: Date.now(),
      scale: DEFAULT_PET_SCALE,
    };
    await fs.writeFile(path.join(temporary, 'metadata.json'), `${JSON.stringify(record, null, 2)}\n`, 'utf8');
    await fs.rename(temporary, destination);
    registry.pets.push(record);
    await writeRegistry(registry);
    return getPetForWindow(record.slug);
  } catch (error) {
    await fs.rm(temporary, { recursive: true, force: true });
    throw error;
  }
}

async function setEnabled(slugInput, enabled) {
  const slug = safeSlug(slugInput);
  const registry = await readRegistry();
  const record = registry.pets.find((pet) => pet.slug === slug);
  if (!record) throw new Error('Pet is not downloaded');
  record.enabled = Boolean(enabled);
  await writeRegistry(registry);
  return getPetForWindow(record.slug);
}

async function removePet(slugInput) {
  const slug = safeSlug(slugInput);
  const registry = await readRegistry();
  const index = registry.pets.findIndex((pet) => pet.slug === slug);
  if (index < 0) return false;
  await fs.rm(petDirectory(slug), { recursive: true, force: true });
  registry.pets.splice(index, 1);
  await writeRegistry(registry);
  return true;
}

async function openPetFolder(slugInput) {
  const slug = safeSlug(slugInput);
  const registry = await readRegistry();
  if (!registry.pets.some((pet) => pet.slug === slug)) throw new Error('Pet is not downloaded');
  return petDirectory(slug);
}

async function getPetPreferences(slugInput) {
  const slug = safeSlug(slugInput);
  const registry = await readRegistry();
  const record = registry.pets.find((pet) => pet.slug === slug);
  if (!record) return null;
  return {
    scale: typeof record.scale === 'number' ? record.scale : DEFAULT_PET_SCALE,
    window: record.window,
  };
}

async function savePetPreferences(slugInput, input) {
  const slug = safeSlug(slugInput);
  const registry = await readRegistry();
  const record = registry.pets.find((pet) => pet.slug === slug);
  if (!record) throw new Error('Pet is not downloaded');
  if (typeof input?.scale === 'number' && Number.isFinite(input.scale)) {
    record.scale = Math.min(1.2, Math.max(0.18, input.scale));
  }
  const bounds = input?.window;
  if (bounds && [bounds.x, bounds.y, bounds.width, bounds.height].every(Number.isFinite)) {
    record.window = {
      x: Math.round(bounds.x),
      y: Math.round(bounds.y),
      width: Math.round(bounds.width),
      height: Math.round(bounds.height),
    };
  }
  await writeRegistry(registry);
  return publicRecord(record);
}

function normalizePetDefinition(value) {
  const frame = value?.frame && typeof value.frame === 'object' ? value.frame : {};
  const width = Number(frame.width);
  const height = Number(frame.height);
  const columns = Number(frame.columns);
  const rows = Number(frame.rows);
  const normalizedFrame = {
    width: Number.isInteger(width) && width > 0 && width <= 1024 ? width : 192,
    height: Number.isInteger(height) && height > 0 && height <= 1024 ? height : 208,
    columns: Number.isInteger(columns) && columns > 0 && columns <= 32 ? columns : 8,
    rows: Number.isInteger(rows) && rows > 0 && rows <= 32 ? rows : 9,
  };
  const frameCount = normalizedFrame.columns * normalizedFrame.rows;
  const animations = {};
  if (value?.animations && typeof value.animations === 'object') {
    for (const [name, animation] of Object.entries(value.animations)) {
      if (!animation || typeof animation !== 'object') continue;
      const frames = Array.isArray(animation.frames)
        ? animation.frames.filter((index) => Number.isInteger(index) && index >= 0 && index < frameCount).slice(0, 256)
        : [];
      if (frames.length === 0) continue;
      const fps = Number(animation.fps);
      animations[name] = {
        frames,
        fps: Number.isFinite(fps) ? Math.min(60, Math.max(1, fps)) : 8,
        loop: animation.loop !== false,
        fallback: typeof animation.fallback === 'string' ? animation.fallback : 'idle',
      };
    }
  }
  return { frame: normalizedFrame, animations };
}

async function getPetForWindow(slugInput) {
  const slug = safeSlug(slugInput);
  const registry = await readRegistry();
  const record = registry.pets.find((pet) => pet.slug === slug);
  if (!record) return null;
  const image = await fs.readFile(path.join(petDirectory(slug), record.spriteFile));
  let definition = normalizePetDefinition(null);
  try {
    definition = normalizePetDefinition(JSON.parse(await fs.readFile(path.join(petDirectory(slug), 'pet.json'), 'utf8')));
  } catch { }
  const mime = record.spriteFile.endsWith('.png') ? 'image/png' : 'image/webp';
  return { ...publicRecord(record), definition, imageDataUrl: `data:${mime};base64,${image.toString('base64')}` };
}

module.exports = {
  fetchCatalog,
  fetchPetPreview,
  listDownloaded,
  downloadPet,
  setEnabled,
  removePet,
  openPetFolder,
  getPetPreferences,
  savePetPreferences,
  getPetForWindow,
};
