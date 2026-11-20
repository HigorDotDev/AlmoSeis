'use client';

import Link from 'next/link';
import type { PublicStore } from '@/lib/api/types';
import { addLine, itemCount, newLineId, previewCart } from '@/lib/cart';
import { useCart, useLiveStore, useSavedOrders } from '@/lib/hooks';
import { formatApiMoney, formatBRL } from '@/lib/money';
import { formatTime } from '@/lib/texts';
import { MarmitaBuilder } from './MarmitaBuilder';
import { StoreHeader, StoreStatusNotice } from './StoreHeader';
import { PageFooter } from './PageFooter';

export function StoreClient({ initialStore }: { initialStore: PublicStore }) {
  const { store } = useLiveStore(initialStore);
  const { cart, update, hydrated } = useCart(store.slug);
  const ongoing = useSavedOrders(store.slug).filter((o) => !o.finished);
  const menu = store.status === 'OPEN' || store.status === 'PAUSED' ? store.menu : null;
  const readOnly = store.status !== 'OPEN';
  const preview = menu ? previewCart(cart, menu) : null;
  const count = itemCount(cart);

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

        {menu && (
          <>
            <MarmitaBuilder
              key={store.businessDate ?? 'menu'}
              menu={menu}
              limits={store.limits}
              readOnly={readOnly}
              onAdd={(line) => update((c) => addLine(c, line))}
            />

            {menu.beverages.length > 0 && (
              <section className="card" aria-labelledby="bebidas-title">
                <h2 id="bebidas-title">Bebidas</h2>
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
                              update((c) => addLine(c, { kind: 'BEVERAGE', id: newLineId(), productId: b.productId, quantity: 1 }))
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
        )}

        <PageFooter />
      </main>

      {hydrated && count > 0 && menu && preview && (
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
