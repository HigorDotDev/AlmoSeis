import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import type { PublicStore } from '@/lib/api/types';
import { DEFAULT_LIMITS } from '@/lib/cart';
import { pageTabs, type PageTab } from '@/lib/fixed-menu';
import { fixedMenu } from '@/lib/test-fixtures';
import { FixedItemBuilder } from './FixedItemBuilder';
import { FixedMenuPanel } from './FixedMenuPanel';
import { MenuTabs } from './MenuTabs';
import { StoreClient } from './StoreClient';

vi.mock('next/navigation', () => ({ useRouter: () => ({ refresh: () => {}, replace: () => {}, push: () => {} }) }));

const noop = () => {};
const text = (html: string) => html.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ');

function renderPage(sections: ('MARMITA' | 'A_LA_CARTE')[]) {
  const tabs = pageTabs(sections, fixedMenu);
  return renderToStaticMarkup(
    <MenuTabs
      tabs={tabs}
      selectedKey=""
      onSelect={noop}
      renderPanel={(tab: PageTab) =>
        tab.kind === 'MARMITA' ? (
          <p>Monte sua marmita</p>
        ) : (
          <FixedMenuPanel tab={tab} fixedMenu={fixedMenu} readOnly={false} onChoose={noop} onQuickAddBeverage={noop} />
        )
      }
    />,
  );
}

describe('seções da página', () => {
  it('as duas seções: abas Marmitas, categorias, Bebidas e Combos; só a primeira selecionada', () => {
    const html = renderPage(['MARMITA', 'A_LA_CARTE']);
    expect(html).toContain('role="tablist"');
    const tabs = [...html.matchAll(/role="tab"[^>]*aria-selected="(true|false)"[^>]*>([^<]+)</g)].map((m) => [m[2], m[1]]);
    expect(tabs).toEqual([
      ['Marmitas', 'true'],
      ['Lanches', 'false'],
      ['Porções', 'false'],
      ['Bebidas', 'false'],
      ['Combos', 'false'],
    ]);
    // Todos os painéis vão no HTML do servidor; os outros ficam escondidos.
    expect(html.match(/role="tabpanel"/g)).toHaveLength(5);
    expect(html.match(/role="tabpanel"[^>]*hidden=""/g)).toHaveLength(4);
    expect(text(html)).toContain('Monte sua marmita');
  });

  it('lanchonete: pratos com preço de site, bebidas e combos', () => {
    const page = text(renderPage(['A_LA_CARTE']));
    expect(page).toContain('X-Burguer');
    expect(page).toContain('R$ 23,90');
    expect(page).toContain('a partir de R$ 13,00');
    expect(page).toContain('Água 500 ml');
    expect(page).toContain('Refrigerante');
    expect(page).toContain('R$ 34,90');
    expect(page).toContain('X-Burguer + Batata frita P + Refrigerante Lata 350 ml');
    expect(page).not.toContain('Marmitas');
    const html = renderPage(['A_LA_CARTE']);
    expect(html).toContain('aria-label="Escolher X-Burguer"');
    // Bebida de um tamanho e sem sabor entra direto.
    expect(html).toContain('aria-label="Adicionar Água 500 ml"');
    expect(html).toContain('aria-label="Escolher Refrigerante"');
  });

  it('uma aba só: sem a barra de abas', () => {
    const html = renderToStaticMarkup(
      <MenuTabs tabs={pageTabs(['MARMITA'], null)} selectedKey="" onSelect={noop} renderPanel={() => <p>Monte sua marmita</p>} />,
    );
    expect(html).toBe('<p>Monte sua marmita</p>');
  });
});

describe('montagem do item', () => {
  const render = (kind: 'DISH' | 'BEVERAGE' | 'COMBO', id: string) =>
    text(
      renderToStaticMarkup(
        <FixedItemBuilder
          selection={{ kind, id }}
          fixedMenu={fixedMenu}
          limits={DEFAULT_LIMITS}
          readOnly={false}
          titleId="t"
          onAdd={noop}
          onClose={noop}
        />,
      ),
    );

  it('prato: tamanho, adicionais do grupo e observação', () => {
    const batata = render('DISH', 'batata');
    expect(batata).toContain('Tamanho');
    expect(batata).toContain('R$ 19,00');
    expect(batata).not.toContain('Adicionais de lanche');
    const x = render('DISH', 'xburguer');
    expect(x).not.toContain('Tamanho');
    expect(x).toContain('Adicionais de lanche');
    expect(x).toContain('Bacon + R$ 4,00');
    expect(x).toContain('Observação (opcional)');
    expect(x).toContain('Adicionar ao pedido · R$ 23,90');
  });

  it('bebida: tamanho e sabor com acréscimo', () => {
    const refri = render('BEVERAGE', 'refri');
    expect(refri).toContain('Sabor');
    expect(refri).toContain('Coca-Cola Zero + R$ 0,50');
    // Sem sabor escolhido, o botão não mostra preço.
    expect(refri).toContain('Adicionar ao pedido Cancelar');
  });

  it('combo: adicionais e observação por prato, sabor por bebida', () => {
    const c = render('COMBO', 'combo');
    expect(c).toContain('Sabor: Refrigerante Lata 350 ml');
    expect(c).toContain('Adicionais de lanche (X-Burguer)');
    expect(c).toContain('Observação para X-Burguer (opcional)');
    expect(c).toContain('Observação para Batata frita P (opcional)');
  });

  it('item que saiu do cardápio', () => {
    expect(render('COMBO', 'sumiu')).toContain('Item indisponível');
  });
});

describe('bebida de preço zero na página (§6.5, §6.18)', () => {
  const store = {
    slug: 'ana',
    status: 'OPEN',
    name: 'Marmitaria da Ana',
    sections: ['MARMITA'],
    address: null,
    contactPhone: null,
    hoursText: null,
    pickupAvailable: true,
    deliveryAvailable: false,
    deliveryRule: null,
    privacy: null,
    businessDate: '2026-11-23',
    menu: {
      marmitaSizes: [{ id: 'size-m', name: 'M', price: '20.00' }],
      mainDishes: [{ productId: 'frango', name: 'Frango grelhado', description: null, imageUrl: null, isAvailable: true }],
      sideDishes: [],
      addons: [],
      beverages: [
        { productId: 'coca', name: 'Coca-Cola 350ml', description: null, imageUrl: null, price: '6.00', isAvailable: true },
        { productId: 'agua', name: 'Água da casa', description: null, imageUrl: null, price: '0.00', isAvailable: true },
      ],
    },
    fixedMenu: null,
    limits: DEFAULT_LIMITS,
  } as PublicStore;

  it('mostra "R$ 0,00", como os demais preços, e oferece o botão de adicionar', () => {
    const html = renderToStaticMarkup(<StoreClient initialStore={store} />);
    const page = text(html);
    expect(page).toContain('Água da casa R$ 0,00');
    expect(page).toContain('Coca-Cola 350ml R$ 6,00');
    expect(page).not.toContain('Grátis');
    expect(html).toContain('aria-label="Adicionar Água da casa"');
  });
});
