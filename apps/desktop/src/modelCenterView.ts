import type {
  ModelProviderInstanceDto,
  ModelProviderReadinessDto,
  ModelReadinessReceiptDto
} from './api'
import type { ProviderTemplate } from './providerTemplates'

export type ModelCenterFilter = 'all' | 'issues' | 'configured' | 'available'

interface ModelCenterRowBase {
  key: string
  driver: string
  display_name: string
  connection_kind: string
  model: string
  template?: ProviderTemplate
}

export interface ModelCenterInstanceRow extends ModelCenterRowBase {
  kind: 'instance'
  provider: ModelProviderInstanceDto
  readiness?: ModelProviderReadinessDto
}

export interface ModelCenterTemplateRow extends ModelCenterRowBase {
  kind: 'template'
  template: ProviderTemplate
}

export type ModelCenterRow = ModelCenterInstanceRow | ModelCenterTemplateRow

function readinessRank(readiness?: ModelProviderReadinessDto) {
  if (readiness?.status === 'blocked') return 0
  if (readiness?.status === 'warning') return 1
  return 2
}

export function buildModelCenterRows(
  providers: ModelProviderInstanceDto[],
  templates: ProviderTemplate[],
  modelReadiness: ModelReadinessReceiptDto | null,
  translate: (key: string) => string = (key) => key
): ModelCenterRow[] {
  const templateByDriver = new Map(templates.map((template) => [template.driver, template]))
  const readinessById = new Map(
    (modelReadiness?.providers ?? []).map((readiness) => [readiness.provider_instance_id, readiness])
  )

  const instanceRows = providers
    .map((provider, index) => ({
      index,
      rank: readinessRank(readinessById.get(provider.id)),
      row: {
        kind: 'instance' as const,
        key: `instance:${provider.id}`,
        driver: provider.driver,
        display_name: provider.display_name,
        connection_kind: provider.connection_kind,
        model: provider.model ?? '',
        provider,
        readiness: readinessById.get(provider.id),
        template: templateByDriver.get(provider.driver)
      }
    }))
    .sort((left, right) => left.rank - right.rank || left.index - right.index)
    .map(({ row }) => row)

  const configuredDrivers = new Set(providers.map((provider) => provider.driver))
  const templateRows: ModelCenterTemplateRow[] = templates
    .filter((template) => !configuredDrivers.has(template.driver))
    .map((template) => ({
      kind: 'template',
      key: `template:${template.driver}`,
      driver: template.driver,
      display_name: translate(template.display_name_key),
      connection_kind: template.connection_kind,
      model: template.default_model ?? '',
      template
    }))

  return [...instanceRows, ...templateRows]
}

export function filterModelCenterRows(
  rows: ModelCenterRow[],
  filter: ModelCenterFilter,
  query: string
) {
  const normalizedQuery = query.trim().toLocaleLowerCase()

  return rows.filter((row) => {
    if (filter === 'issues' && (row.kind !== 'instance' || !['blocked', 'warning'].includes(row.readiness?.status ?? ''))) {
      return false
    }
    if (filter === 'configured' && row.kind !== 'instance') return false
    if (filter === 'available' && row.kind !== 'template') return false
    if (!normalizedQuery) return true

    return [row.display_name, row.driver, row.connection_kind, row.model]
      .some((value) => value.toLocaleLowerCase().includes(normalizedQuery))
  })
}
