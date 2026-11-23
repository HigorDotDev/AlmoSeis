'use client';

import type { PublicBeverageType, PublicFixedMenu } from '@/lib/api/types';
import { beveragePriceLabel, dishPriceLabel, isSingleChoiceBeverage, type PageTab } from '@/lib/fixed-menu';
import { formatApiMoney } from '@/lib/money';

/** O que o cliente tocou para montar: um prato, um tipo de bebida ou um combo (pelo id, relido do cardápio vivo). */
export type FixedSelection = { kind: 'DISH' | 'BEVERAGE' | 'COMBO'; id: string };

interface Props {
  tab: Exclude<PageTab, { kind: 'MARMITA' }>;
  fixedMenu: PublicFixedMenu;
  /** PAUSED: o cardápio aparece só para consulta (ADR-0044 item 6). */
  readOnly: boolean;
  onChoose: (selection: FixedSelection) => void;
  /** Bebida de um tamanho só e sem sabor: adiciona direto. */
  onQuickAddBeverage: (type: PublicBeverageType) => void;
}

function Thumb({ src }: { src: string | null }) {
  return src ? <img className="dish-img" src={src} alt="" loading="lazy" width={56} height={56} /> : null;
}

/** Uma aba do cardápio fixo: pratos da categoria, Bebidas ou Combos. Itens pausados nem chegam (§6.19). */
export function FixedMenuPanel({ tab, fixedMenu, readOnly, onChoose, onQuickAddBeverage }: Props) {
  const headingId = `${tab.key}-title`;

  if (tab.kind === 'CATEGORY') {
    const category = fixedMenu.categories.find((c) => c.id === tab.categoryId);
    return (
      <section className="card" aria-labelledby={headingId}>
        <h2 id={headingId}>{tab.label}</h2>
        <ul className="list">
          {(category?.dishes ?? []).map((dish) => (
            <li key={dish.id} className="row-between">
              <span className="row">
                <Thumb src={dish.imageUrl} />
                <span>
                  {dish.name}
                  {dish.description && <span className="option-desc">{dish.description}</span>}
                  <span className="option-desc price">{dishPriceLabel(dish)}</span>
                </span>
              </span>
              <button type="button" className="btn" onClick={() => onChoose({ kind: 'DISH', id: dish.id })} aria-label={`Escolher ${dish.name}`}>
                Escolher
              </button>
            </li>
          ))}
        </ul>
      </section>
    );
  }

  if (tab.kind === 'BEVERAGES') {
    return (
      <section className="card" aria-labelledby={headingId}>
        <h2 id={headingId}>{tab.label}</h2>
        <ul className="list">
          {fixedMenu.beverages
            .filter((b) => b.sizes.length > 0)
            .map((type) => {
              const single = isSingleChoiceBeverage(type);
              const name = single ? `${type.name} ${type.sizes[0]!.name}` : type.name;
              return (
                <li key={type.id} className="row-between">
                  <span>
                    {name}
                    <span className="option-desc">
                      {single ? null : `${type.sizes.map((s) => s.name).join(', ')} · `}
                      <span className="price">{beveragePriceLabel(type)}</span>
                    </span>
                  </span>
                  {single ? (
                    <button type="button" className="btn" disabled={readOnly} onClick={() => onQuickAddBeverage(type)} aria-label={`Adicionar ${name}`}>
                      Adicionar
                    </button>
                  ) : (
                    <button type="button" className="btn" onClick={() => onChoose({ kind: 'BEVERAGE', id: type.id })} aria-label={`Escolher ${type.name}`}>
                      Escolher
                    </button>
                  )}
                </li>
              );
            })}
        </ul>
      </section>
    );
  }

  return (
    <section className="card" aria-labelledby={headingId}>
      <h2 id={headingId}>{tab.label}</h2>
      <ul className="list">
        {fixedMenu.combos.map((combo) => (
          <li key={combo.id} className="row-between">
            <span className="row">
              <Thumb src={combo.imageUrl} />
              <span>
                {combo.name}
                <span className="option-desc">
                  {combo.description ?? combo.components.map((c) => `${c.quantity > 1 ? `${c.quantity}× ` : ''}${c.name}`).join(' + ')}
                </span>
                <span className="option-desc price">{formatApiMoney(combo.price)}</span>
              </span>
            </span>
            <button type="button" className="btn" onClick={() => onChoose({ kind: 'COMBO', id: combo.id })} aria-label={`Escolher ${combo.name}`}>
              Escolher
            </button>
          </li>
        ))}
      </ul>
    </section>
  );
}
