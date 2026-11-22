import { describe, expect, it } from 'vitest'
import { findTemplate, PROVIDER_TEMPLATES } from './providerTemplates'

describe('provider templates', () => {
  it('keeps provider drivers unique', () => {
    const drivers = PROVIDER_TEMPLATES.map((template) => template.driver)

    expect(new Set(drivers).size).toBe(drivers.length)
  })

  it('includes popular cloud presets and extended ACP/CLI harnesses in lockstep with HarnessCatalog', () => {
    for (const driver of ['zhipu', 'moonshot', 'minimax', 'siliconflow', 'volcengine']) {
      const tpl = findTemplate(driver)
      expect(tpl, `${driver} cloud template missing`).toBeDefined()
      expect(tpl?.category).toBe('cloud-api')
      expect(tpl?.icon).toContain('<svg')
    }

    for (const driver of ['agy', 'gemini-cli', 'qwen-code', 'minimax-code', 'pi', 'custom-acp']) {
      const tpl = findTemplate(driver)
      expect(tpl, `${driver} harness template missing`).toBeDefined()
      expect(tpl?.category).toBe('agent-cli')
      expect(tpl?.channels?.some((c) => c.channel === 'acp')).toBe(true)
      expect(tpl?.icon).toContain('<svg')
    }
  })
})
