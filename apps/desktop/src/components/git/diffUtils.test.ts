import { describe, expect, it } from 'vitest'
import { buildFileTree, compactTree, collectNodePaths } from './diffUtils'

describe('diffUtils tree operations', () => {
  it('builds a hierarchical and compact tree from flat file list', () => {
    const files = [
      { path: 'src/components/git/GitPanel.vue', status: 'modified', is_staged: false },
      { path: 'src/components/git/diffUtils.ts', status: 'modified', is_staged: false },
      { path: 'docs/readme.md', status: 'added', is_staged: true },
      { path: 'package.json', status: 'modified', is_staged: false }
    ]

    const tree = buildFileTree(files, { compact: true })

    // Root should have 2 folders ('docs', 'src/components/git') and 1 file ('package.json')
    expect(tree).toHaveLength(3)

    // Folders come first
    const folderNames = tree.filter((n) => n.isFolder).map((n) => n.name)
    expect(folderNames).toContain('docs')
    expect(folderNames).toContain('src/components/git')

    const fileNodes = tree.filter((n) => !n.isFolder).map((n) => n.name)
    expect(fileNodes).toEqual(['package.json'])

    // Check compacted folder children
    const gitFolder = tree.find((n) => n.name === 'src/components/git')
    expect(gitFolder).toBeDefined()
    expect(gitFolder?.children).toHaveLength(2)
    expect(gitFolder?.children?.map((c) => c.name)).toEqual(['diffUtils.ts', 'GitPanel.vue'])
  })

  it('collectNodePaths retrieves all nested file paths', () => {
    const files = [
      { path: 'src/a.ts' },
      { path: 'src/sub/b.ts' },
      { path: 'src/sub/c.ts' }
    ]

    const tree = buildFileTree(files, { compact: false })
    const srcFolder = tree.find((n) => n.name === 'src')
    expect(srcFolder).toBeDefined()

    const paths = collectNodePaths(srcFolder!)
    expect(paths).toHaveLength(3)
    expect(paths).toContain('src/a.ts')
    expect(paths).toContain('src/sub/b.ts')
    expect(paths).toContain('src/sub/c.ts')
  })
})
