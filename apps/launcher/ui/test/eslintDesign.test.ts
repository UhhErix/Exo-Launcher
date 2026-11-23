// eslint.config.js design bans, run through ESLint itself on source text it should accept or refuse. Ways it could fail:
// a backdrop class kept in a const or another prop passes; an inline literal colour passes; a token or #000 is refused.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import path from 'node:path'
import { ESLint } from 'eslint'

const root = path.resolve(import.meta.dirname, '..')
const eslint = new ESLint({ cwd: root })

async function lint(code: string) {
  const [result] = await eslint.lintText(code, { filePath: path.join(root, 'src', 'zz.tsx') })
  return result.messages.filter((m) => m.ruleId === 'no-restricted-syntax').map((m) => m.message)
}

test('a backdrop class (Liquid Glass) is not refused, wherever the string is kept', async () => {
  assert.equal((await lint("export const a = () => <div className='backdrop-blur-md' />")).length, 0)
  assert.equal((await lint("const k = 'backdrop-blur-md'\nexport const a = () => <div className={k} />")).length, 0)
  assert.equal((await lint("export const a = () => <div style={{ backdropFilter: 'blur(4px)' }} />")).length, 0)
})

test('an inline style never paints a literal colour; tokens, #000 and data pass', async () => {
  for (const style of ["{ color: '#fff' }", "{ background: 'rgb(1 2 3)' }", "{ borderColor: 'red' }", '{ background: `hsl(0 0% 50%)` }', "{ fill: 'white' }"])
    assert.equal((await lint(`export const a = () => <div style={${style}} />`)).length, 1, style)
  for (const style of ["{ color: 'var(--color-fg)' }", "{ background: '#000' }", '{ background: colour }', "{ filter: mono ? 'brightness(0)' : tile?.ink === 'white' ? 'invert(1)' : undefined }"])
    assert.equal((await lint(`export const a = ({ colour, mono, tile }: any) => <div style={${style}} />`)).length, 0, style)
})
