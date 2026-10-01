import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// The committed projection lets Gateway build independently. Regeneration uses
// the Core snapshot; an alternate input can be supplied by a standalone checkout.
const here = dirname(fileURLToPath(import.meta.url));
const source = resolve(here, process.env.TINA_CHAT_OPENAPI_SOURCE ?? '../../TinadecCore/tests/__snapshots__/openapi.core.json');
const destination = resolve(here, '../src/contracts/tina-chat.openapi.json');
const core = JSON.parse(readFileSync(source, 'utf8'));
const prefix = '/api/v1/tina-chat';
const paths = Object.fromEntries(Object.entries(core.paths).filter(([path]) => path === prefix || path.startsWith(prefix + '/')));
if (Object.keys(paths).length === 0) throw new Error('The Core snapshot contains no TinaChat routes. Regenerate the Core snapshot first.');

const schemas = {};
function collect(value) {
  if (Array.isArray(value)) { value.forEach(collect); return; }
  if (!value || typeof value !== 'object') return;
  if (typeof value.$ref === 'string') {
    const name = value.$ref.match(/^#\/components\/schemas\/(.+)$/)?.[1];
    if (!name) throw new Error(`Unsupported reference: ${value.$ref}`);
    if (!Object.hasOwn(schemas, name)) {
      if (!Object.hasOwn(core.components.schemas, name)) throw new Error(`Missing schema: ${name}`);
      schemas[name] = core.components.schemas[name];
      collect(schemas[name]);
    }
  }
  Object.values(value).forEach(collect);
}
collect(paths);

// Prefix generic Core schema names so existing Gateway DTOs keep their identity.
function project(value) {
  if (Array.isArray(value)) return value.map(project);
  if (!value || typeof value !== 'object') return value;
  // Core emits OAS 3.1; Gateway documents OAS 3.0.3. Schema Object.type
  // must be a string in 3.0, and null is represented by nullable on a type.
  // https://spec.openapis.org/oas/v3.0.3.html#schema-object
  if (Array.isArray(value.type)) {
    const { type, ...rest } = value;
    const concrete = [...new Set(type.filter(item => item !== 'null'))];
    const nullable = type.includes('null');
    if (concrete.length === 0) throw new Error('A null-only union needs an explicit OpenAPI 3.0 projection.');
    if (concrete.length === 1) return project({ ...rest, type: concrete[0], ...(nullable ? { nullable: true } : {}) });
    const alternatives = concrete.map((item, index) => ({ type: item, ...(nullable && index === 0 ? { nullable: true } : {}) }));
    return project({ ...rest, allOf: [...(rest.allOf ?? []), { anyOf: alternatives }] });
  }
  if (value.type === 'null') return project({ ...value, type: 'string', nullable: true, enum: [null] });
  return Object.fromEntries(Object.keys(value).sort().map(key => {
    const item = value[key];
    if (key === '$ref' && typeof item === 'string') {
      const name = item.slice('#/components/schemas/'.length);
      return [key, '#/components/schemas/' + (name.startsWith('TinaChat') ? name : 'TinaChat' + name)];
    }
    return [key, project(item)];
  }));
}
const renamedSchemas = Object.fromEntries(Object.entries(schemas).map(([name, schema]) =>
  [name.startsWith('TinaChat') ? name : 'TinaChat' + name, schema]));
const serialized = JSON.stringify(project({ paths, components: { schemas: renamedSchemas } }), null, 2) + '\n';
if (process.argv.includes('--check')) {
  if (readFileSync(destination, 'utf8') !== serialized) throw new Error('TinaChat contract drift. Run generate:tina-chat-contract.');
  console.log('TinaChat contract matches the Core snapshot.');
} else {
  mkdirSync(dirname(destination), { recursive: true });
  writeFileSync(destination, serialized, 'utf8');
  console.log(`Generated TinaChat contract: ${Object.keys(paths).length} paths, ${Object.keys(schemas).length} schemas.`);
}
