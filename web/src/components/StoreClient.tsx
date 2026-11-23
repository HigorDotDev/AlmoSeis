'use client';

import Link from 'next/link';
import { useEffect, useRef, useState } from 'react';
import type { PublicBeverageType, PublicStore, StoreSection } from '@/lib/api/types';
import {
  addLine,
  canAddLine,
  cartOrderType,
  EMPTY_CART,
  itemCount,
  newLineId,
  ORDER_TYPE_TEXT,
  previewCart,
  type CartLine,
} from '@/lib/cart';
import { catalogOf, pageTabs, storeSections, type PageTab } from '@/lib/fixed-menu';
import { useCart, useLiveStore, useSavedOrders } from '@/lib/hooks';
import { formatApiMoney, formatBRL } from '@/lib/money';
import { loadCart } from '@/lib/storage';
import { formatTime } from '@/lib/texts';
import { FixedItemBuilder } from './FixedItemBuilder';
import { FixedMenuPanel, type FixedSelection } from './FixedMenuPanel';
import { MarmitaBuilder } from './MarmitaBuilder';
import { MenuTabs } from './MenuTabs';
import { StoreHeader, StoreStatusNotice } from './StoreHeader';
import { PageFooter } from './PageFooter';

interface Conflict {
  line: CartLine;
  current: StoreSection;
  announcement: string;
}

export function StoreClient({ initialStore }: { initialStore: PublicStore }) {
  const { store } = useLiveStore(initialStore);
  const { cart, update, hydrated } = useCart(store.slug);
  const ongoing = useSavedOrders(store.slug).filter((o) => !o.finished);
  const catalog = catalogOf(store);
  const { menu, fixedMenu } = catalog;
  const visible = store.status === 'OPEN' || store.status === 'PAUSED';
  const readOnly = store.status !== 'OPEN';
  const tabs = visible ? pageTabs(storeSections(store), fixedMenu) : [];
  const preview = previewCart(cart, catalog);
  const count = itemCount(cart);
  const cartType = hydrated ? cartOrderType(cart) : null;

  const [tabKey, setTabKey] = useState('');
  const [selection, setSelection] = useState<FixedSelection | null>(null);
  const [conflict, setConflict] = useState<Conflict | null>(null);
  const [announcement, setAnnouncement] = useState('');
  const itemDialog = useRef<HTMLDialogElement>(null);
  const conflictDialog = useRef<HTMLDialogElement>(null);

  useEffect(() => {
    if (selection && !itemDialog.current?.open) itemDialog.current?.showModal();
  }, [selection]);
  useEffect(() => {
    if (conflict && !conflictDialog.current?.open) conflictDialog.current?.showModal();
  }, [conflict]);

  /** Um tipo por vez (Q-317): de outra seção com o carrinho não vazio → avisa e oferece esvaziar. */
  function tryAdd(line: CartLine, text: string): boolean {
    // O guardado, não o instantâneo da renderização (antes da hidratação ele ainda está vazio).
    const check = canAddLine(loadCart(store.slug), line);
    if (check.ok) {
      update((c) => addLine(c, line, store.limits.maxQuantityPerLine));
      setAnnouncement(text);
      return true;
    }
    itemDialog.current?.close();
    setConflict({ line, current: check.current, announcement: text });
    return false;
  }

  function replaceCart() {
    if (!conflict) return;
    const { line, announcement: text } = conflict;
    update(() => addLine(EMPTY_CART, line, store.limits.maxQuantityPerLine));
    setAnnouncement(`Pedido anterior esvaziado. ${text}`);
    conflictDialog.current?.close();
  }

  function quickAddBeverage(type: PublicBeverageType) {
    const size = type.sizes[0]!;
    tryAdd(
      { kind: 'FIXED_BEVERAGE', id: newLineId(), sizeId: size.id, flavorId: null, quantity: 1 },
      `Adicionado ao pedido: ${type.name} ${size.name}.`,
    );
  }

  /** O carrinho tem outro tipo de pedido: avisa antes de o cliente montar. */
  function otherTypeNotice(section: StoreSection) {
    if (!cartType || cartType === section) return null;
    return (
      <p className="notice notice-info">
        Seu pedido tem {ORDER_TYPE_TEXT[cartType]}. O carrinho aceita um tipo de pedido por vez: envie esse pedido primeiro
        ou esvazie-o para pedir daqui.
      </p>
    );
  }

  function renderPanel(tab: PageTab) {
    if (tab.kind === 'MARMITA') {
      if (!menu) {
        // Só marmita: o aviso geral (StoreStatusNotice) já diz que o cardápio não saiu.
        return tabs.length > 1 ? (
          <div className="notice notice-warning" role="status">
            <strong>O cardápio de marmitas de hoje ainda não foi publicado.</strong> Volte daqui a pouco.
          </div>
        ) : null;
      }
      return (
        <>
          {otherTypeNotice('MARMITA')}
          <MarmitaBuilder
            key={store.businessDate ?? 'menu'}
            menu={menu}
            limits={store.limits}
            readOnly={readOnly}
            onAdd={(line) => tryAdd(line, `${line.quantity > 1 ? `${line.quantity} marmitas adicionadas` : 'Marmita adicionada'} ao pedido.`)}
          />
          {menu.beverages.length > 0 && (
            <section className="card" aria-labelledby="bebidas-title">
              <h2 id="bebidas-title">{tabs.length > 1 ? 'Bebidas para a marmita' : 'Bebidas'}</h2>
              <ul className="list">
                {menu.beverages.map((b) => {
                  const inCart = cart.lines.find((l) => l.kind === 'BEVERAGE' && l.productId === b.productId)?.quantity ?? 0;
                  return (
                    <li key={b.productId} className="row-between">
                      <span className="row">
                        {b.imageUrl && <img className="dish-img" src={b.imageUrl} alt="" loading="lazy" width={56} height={56} />}
                        <span>
                          {b.name}
                          <span className="option-desc">
                            {formatApiMoney(b.price)}
                            {inCart > 0 && ` · ${inCart} no pedido`}
                          </span>
                        </span>
                      </span>
                      {b.isAvailable ? (
                        <button
                          type="button"
                          className="btn"
                          disabled={readOnly || inCart >= store.limits.maxQuantityPerLine}
                          onClick={() =>
                            tryAdd({ kind: 'BEVERAGE', id: newLineId(), productId: b.productId, quantity: 1 }, `Adicionado ao pedido: ${b.name}.`)
                          }
                          aria-label={`Adicionar ${b.name}`}
                        >
                          Adicionar
                        </button>
                      ) : (
                        <span className="badge">Acabou</span>
                      )}
                    </li>
                  );
                })}
              </ul>
            </section>
          )}
        </>
      );
    }
    if (!fixedMenu) return null;
    return (
      <>
        {otherTypeNotice('A_LA_CARTE')}
        <FixedMenuPanel tab={tab} fixedMenu={fixedMenu} readOnly={readOnly} onChoose={setSelection} onQuickAddBeverage={quickAddBeverage} />
      </>
    );
  }

  return (
    <>
      <main id="conteudo" className="page">
        <StoreHeader store={store} />

        {ongoing.length > 0 && (
          <div className="notice notice-success">
            <p>
              <strong>Acompanhar pedido em andamento</strong>
            </p>
            <ul className="list">
              {ongoing.map((o) => (
                <li key={o.trackingToken}>
                  <Link href={`/${store.slug}/pedido/${o.trackingToken}`}>
                    Pedido das {formatTime(o.submittedAt)} · {formatApiMoney(o.totalAmount)}
                  </Link>
                </li>
              ))}
            </ul>
          </div>
        )}

        <StoreStatusNotice store={store} />

        <MenuTabs tabs={tabs} selectedKey={tabKey} onSelect={setTabKey} renderPanel={renderPanel} />

        <p className="visually-hidden" aria-live="polite">
          {announcement}
        </p>

        <PageFooter />
      </main>

      <dialog ref={itemDialog} className="builder-dialog" aria-labelledby="montagem-title" onClose={() => setSelection(null)}>
        {selection && fixedMenu && (
          <FixedItemBuilder
            key={`${selection.kind}-${selection.id}`}
            selection={selection}
            fixedMenu={fixedMenu}
            limits={store.limits}
            readOnly={readOnly}
            titleId="montagem-title"
            onAdd={(line, text) => {
              if (tryAdd(line, text)) itemDialog.current?.close();
            }}
            onClose={() => itemDialog.current?.close()}
          />
        )}
      </dialog>

      <dialog ref={conflictDialog} aria-labelledby="troca-title" onClose={() => setConflict(null)}>
        {conflict && (
          <>
            <h2 id="troca-title">Seu pedido já tem {ORDER_TYPE_TEXT[conflict.current]}</h2>
            <p>
              O carrinho aceita um tipo de pedido por vez. Para adicionar este item, esvazie o pedido atual (ou envie-o primeiro
              e faça outro pedido depois).
            </p>
            <div className="stack">
              <button type="button" className="btn btn-primary btn-block" onClick={replaceCart}>
                Esvaziar o pedido e adicionar
              </button>
              <button type="button" className="btn btn-block" onClick={() => conflictDialog.current?.close()}>
                Manter meu pedido
              </button>
            </div>
          </>
        )}
      </dialog>

      {hydrated && count > 0 && (menu || fixedMenu) && (
        <div className="cart-bar">
          <div className="cart-bar-inner">
            <Link href={`/${store.slug}/carrinho`} className="btn btn-primary btn-block">
              Ver pedido · {count} {count === 1 ? 'item' : 'itens'} · <span className="price">{formatBRL(preview.totalCents)}</span>
            </Link>
          </div>
        </div>
      )}
    </>
  );
}
