import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const require = createRequire(path.join(__dirname, '../bench-suite/package.json'));
const ts = require('typescript');

const BLOCKER_PATTERNS = [
  { key: 'dom_measure', re: /\b(getBoundingClientRect|getClientRects|offsetWidth|offsetHeight|clientWidth|clientHeight|scrollWidth|scrollHeight|getComputedStyle)\b/g },
  { key: 'dom_observers', re: /\b(ResizeObserver|IntersectionObserver|MutationObserver)\b/g },
  { key: 'dom_global', re: /\b(document\.|window\.|HTMLElement|SVGElement|Element\.prototype|createPortal|NodeFilter|TreeWalker)\b/g },
  { key: 'canvas_webgl', re: /\b(getContext\s*\(\s*['"](2d|webgl|webgl2)|WebGLRenderingContext|WebGL2RenderingContext|THREE\.|new Renderer|gl\.createShader|gl\.drawArrays)\b/g },
  { key: 'waapi_anim_lib', re: /\b(\.animate\s*\(|requestAnimationFrame|gsap\.|ScrollTrigger|framer-motion|motion\/react|useSpring|useMotionValue|AnimatePresence)\b/g },
  { key: 'css_dynamic', re: /\b(CSSStyleSheet|insertRule|matchMedia|data-\[|@keyframes|backdrop-filter|clip-path|perspective\(|rotateY|rotateX|transform-style)\b/g },
  { key: 'aria_portal_focus', re: /\b(react-aria|@radix-ui\/react-popper|@floating-ui|aria-activedescendant|FocusScope|useOverlayPosition|DismissableLayer|RemoveScroll)\b/g },
  { key: 'dynamic_js', re: /\b(new Proxy|Reflect\.|Object\.defineProperty|eval\(|new Function|cloneElement)\b/g },
];

function walkFiles(dir, exts, out = []) {
  if (!fs.existsSync(dir)) return out;
  for (const ent of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, ent.name);
    if (ent.isDirectory()) {
      if (ent.name === 'node_modules' || ent.name === '__tests__' || ent.name === 'dist-types') continue;
      walkFiles(p, exts, out);
    } else if (exts.some((e) => ent.name.endsWith(e)) && !ent.name.endsWith('.d.ts') && !ent.name.includes('.test.')) {
      out.push(p);
    }
  }
  return out;
}

function analyzeFile(filePath) {
  const code = fs.readFileSync(filePath, 'utf8');
  const hits = {};
  let totalBlockers = 0;
  for (const pat of BLOCKER_PATTERNS) {
    const m = code.match(pat.re);
    if (m && m.length > 0) {
      hits[pat.key] = m.length;
      totalBlockers += m.length;
    }
  }
  const sf = ts.createSourceFile(filePath, code, ts.ScriptTarget.Latest, true, filePath.endsWith('.tsx') || filePath.endsWith('.jsx') ? ts.ScriptKind.TSX : ts.ScriptKind.JS);
  let jsxElements = 0;
  let hooksCount = 0;
  let dynamicSpreadProps = 0;
  function visit(node) {
    if (ts.isJsxElement(node) || ts.isJsxSelfClosingElement(node)) jsxElements++;
    if (ts.isJsxSpreadAttribute(node)) dynamicSpreadProps++;
    if (ts.isCallExpression(node) && ts.isIdentifier(node.expression) && /^use[A-Z]/.test(node.expression.text)) {
      hooksCount++;
    }
    ts.forEachChild(node, visit);
  }
  visit(sf);

  const hasFatal =
    Boolean(hits.dom_measure) ||
    Boolean(hits.dom_observers) ||
    Boolean(hits.dom_global) ||
    Boolean(hits.canvas_webgl) ||
    Boolean(hits.waapi_anim_lib) ||
    Boolean(hits.aria_portal_focus) ||
    Boolean(hits.dynamic_js);

  const pureTranslatable = !hasFatal && dynamicSpreadProps === 0;
  const shimTranslatable = !hits.canvas_webgl && !hits.aria_portal_focus && !hits.dom_observers && !hits.dynamic_js;

  return {
    file: filePath,
    loc: code.split('\n').length,
    jsxElements,
    hooksCount,
    dynamicSpreadProps,
    hits,
    totalBlockers,
    pureTranslatable,
    shimTranslatable,
  };
}

function auditLibrary(name, files) {
  const results = files.map(analyzeFile);
  const blockerTotals = {};
  for (const r of results) {
    for (const [k, v] of Object.entries(r.hits)) {
      blockerTotals[k] = (blockerTotals[k] || 0) + v;
    }
  }
  const total = results.length;
  const pure = results.filter((r) => r.pureTranslatable).length;
  const withShim = results.filter((r) => r.shimTranslatable).length;
  return {
    library: name,
    filesAudited: total,
    totalLoc: results.reduce((s, r) => s + r.loc, 0),
    pureAotSurvive: pure,
    pureAotPct: total > 0 ? Math.round((pure / total) * 1000) / 10 : 0,
    withRuntimeShimSurvive: withShim,
    withRuntimeShimPct: total > 0 ? Math.round((withShim / total) * 1000) / 10 : 0,
    blockerTotals,
  };
}

function compileSimpleTsxToRune(tsxCode) {
  const sf = ts.createSourceFile('sample.tsx', tsxCode, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
  const lines = ['use rune::{col, row, el, text, Color, El};', 'pub fn compiled_view() -> El {'];
  function emitJsx(node, indent = '    ') {
    if (ts.isJsxText(node)) {
      const t = node.text.trim();
      if (!t) return null;
      return `${indent}text(${JSON.stringify(t)})`;
    }
    if (ts.isJsxElement(node) || ts.isJsxSelfClosingElement(node)) {
      const opening = ts.isJsxElement(node) ? node.openingElement : node;
      const tag = opening.tagName.getText(sf);
      let cls = '';
      for (const attr of opening.attributes.properties) {
        if (ts.isJsxAttribute(attr) && attr.name.getText(sf) === 'className' && attr.initializer && ts.isStringLiteral(attr.initializer)) {
          cls = attr.initializer.text;
        }
      }
      let base = cls.includes('flex-row') || (cls.includes('flex') && !cls.includes('flex-col')) ? 'row()' : 'col()';
      if (tag === 'span' || tag === 'p' || tag === 'h1' || tag === 'h2') base = 'el()';
      let chain = `${indent}${base}`;
      if (cls.includes('p-4')) chain += '.p(16.0)';
      if (cls.includes('gap-2')) chain += '.gap(8.0)';
      if (cls.includes('rounded-xl')) chain += '.rounded(12.0)';
      if (cls.includes('bg-zinc-900')) chain += '.bg(Color::rgb(24, 24, 27))';
      if (ts.isJsxElement(node)) {
        for (const ch of node.children) {
          const rendered = emitJsx(ch, indent + '  ');
          if (rendered) chain += `\n${indent}  .child(${rendered.trim()})`;
        }
      }
      return chain;
    }
    return null;
  }
  let body = '    el()';
  function findReturn(n) {
    if (ts.isReturnStatement(n) && n.expression) {
      let expr = n.expression;
      while (ts.isParenthesizedExpression(expr)) expr = expr.expression;
      const e = emitJsx(expr);
      if (e) body = e;
    }
    ts.forEachChild(n, findReturn);
  }
  findReturn(sf);
  lines.push(body);
  lines.push('}');
  return lines.join('\n');
}

const benchNodeModules = path.resolve(__dirname, '../bench-suite/node_modules');
const reactBitsDir = path.resolve(__dirname, '../vendor/react-bits/src/ts-tailwind');
const exoUiSrc = path.resolve(__dirname, '../../../ui/src');

const herouiFiles = walkFiles(path.join(benchNodeModules, '@heroui/react/dist/components'), ['.mjs', '.js']);
const reactAriaFiles = walkFiles(path.join(benchNodeModules, 'react-aria-components/dist'), ['.mjs', '.js']);
const reactBitsFiles = walkFiles(reactBitsDir, ['.tsx']);
const radixDirs = fs
  .readdirSync(path.join(benchNodeModules, '@radix-ui'))
  .filter((d) => d.startsWith('react-'))
  .map((d) => path.join(benchNodeModules, '@radix-ui', d, 'dist'));
const radixFiles = radixDirs.flatMap((d) => walkFiles(d, ['.mjs', '.js']));
const framerFiles = walkFiles(path.join(benchNodeModules, 'framer-motion/dist/es'), ['.mjs', '.js']);
const gsapFiles = walkFiles(path.join(benchNodeModules, 'gsap'), ['.js']).filter((f) => !f.includes('dist'));
const exoUiFiles = walkFiles(exoUiSrc, ['.tsx', '.ts']);

const report = {
  generatedAt: new Date().toISOString(),
  libraries: [
    auditLibrary('@heroui/react (v3.2.8 components)', herouiFiles),
    auditLibrary('react-aria-components (HeroUI engine)', reactAriaFiles),
    auditLibrary('React Bits (all 221 ts-tailwind components)', reactBitsFiles),
    auditLibrary('@radix-ui/react-* (20 shadcn primitives)', radixFiles),
    auditLibrary('framer-motion (v12)', framerFiles),
    auditLibrary('gsap (v3.15.1)', gsapFiles),
    auditLibrary('Exo Launcher ui/src/', exoUiFiles),
  ],
  sampleAotOutput: compileSimpleTsxToRune(`
    export function Card() {
      return (
        <div className="flex flex-col p-4 gap-2 rounded-xl bg-zinc-900">
          <h1>Exo Native</h1>
          <span>Compiled AOT from static JSX</span>
        </div>
      );
    }
  `),
};

const outDir = path.resolve(__dirname, '../../../../../improve/lab-native-react');
fs.mkdirSync(outDir, { recursive: true });
fs.writeFileSync(path.join(outDir, 'route3-aot-audit.json'), JSON.stringify(report, null, 2));
console.log(JSON.stringify(report.libraries, null, 2));
