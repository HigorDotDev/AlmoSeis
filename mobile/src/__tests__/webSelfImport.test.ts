/**
 * 08 §8.15: dentro de `foo.web.ts(x)`, `import … from './foo'` resolve para o próprio arquivo no bundle web
 * (laço). Nem `import type` é aceito: tipos compartilhados ficam em `foo.types.ts`, sem sufixo de plataforma.
 */
import fs from 'fs';
import path from 'path';

const ROOT = path.resolve(__dirname, '..', '..');
const SRC = path.join(ROOT, 'src');
const SCAN = ['src', 'app'].map((d) => path.join(ROOT, d));

function webFiles(dir: string): string[] {
  if (!fs.existsSync(dir)) return [];
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((e) => {
    const full = path.join(dir, e.name);
    if (e.isDirectory()) return e.name === 'node_modules' ? [] : webFiles(full);
    return /\.web\.tsx?$/.test(e.name) ? [full] : [];
  });
}

/** Especificadores de `import`/`export … from`/`require()`/`import()`. */
export function specifiers(source: string): string[] {
  const out: string[] = [];
  const patterns = [/\bfrom\s+['"]([^'"]+)['"]/g, /\bimport\s+['"]([^'"]+)['"]/g, /\b(?:require|import)\s*\(\s*['"]([^'"]+)['"]\s*\)/g];
  for (const re of patterns) {
    for (const m of source.matchAll(re)) out.push(m[1]!);
  }
  return out;
}

/** Caminho absoluto sem extensão nem sufixo de plataforma, ou `null` para pacotes. */
function resolveBase(fromFile: string, spec: string): string | null {
  let target: string;
  if (spec.startsWith('@/')) target = path.join(SRC, spec.slice(2));
  else if (spec.startsWith('.')) target = path.resolve(path.dirname(fromFile), spec);
  else return null;
  return target.replace(/\.(web|native|ios|android)?\.?(tsx?|jsx?)$/, '').replace(/\.(web|native|ios|android)$/, '');
}

function selfImports(file: string): string[] {
  const own = file.replace(/\.web\.tsx?$/, '');
  const source = fs.readFileSync(file, 'utf8');
  return specifiers(source).filter((spec) => {
    const base = resolveBase(file, spec);
    return base !== null && (base === own || base === path.join(own, 'index'));
  });
}

describe('arquivos .web não importam o próprio nome (08 §8.15)', () => {
  const files = SCAN.flatMap(webFiles);

  it('encontra os arquivos .web', () => {
    expect(files.length).toBeGreaterThan(10);
  });

  it.each(files.map((f) => [path.relative(ROOT, f).replace(/\\/g, '/'), f]))('%s', (_name, file) => {
    expect(selfImports(file)).toEqual([]);
  });

  it('detecta o laço por caminho relativo e por alias', () => {
    const file = path.join(SRC, 'lib', 'keyboard.web.ts');
    const own = file.replace(/\.web\.tsx?$/, '');
    for (const spec of ['./keyboard', '@/lib/keyboard', '../lib/keyboard']) {
      expect(resolveBase(file, spec)).toBe(own);
    }
    expect(resolveBase(file, './keyboard.types')).not.toBe(own);
    expect(specifiers("import type { A } from './a';\nexport { B } from \"./b\";\nconst c = require('./c');")).toEqual(['./a', './b', './c']);
  });
});
