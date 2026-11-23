'use client';

import { useId, useState } from 'react';
import type { PublicLimits, PublicMenu } from '@/lib/api/types';
import { newLineId, previewLine, type MarmitaLine } from '@/lib/cart';
import { formatApiMoney, formatBRL } from '@/lib/money';
import { Stepper } from './Stepper';

interface Props {
  menu: PublicMenu;
  limits: PublicLimits;
  /** PAUSED: o cardápio aparece só para consulta (ADR-0044 item 6). */
  readOnly: boolean;
  /** `false`: não entrou agora (o carrinho tem outro tipo de pedido e a página pediu confirmação); a montagem fica. */
  onAdd: (line: MarmitaLine) => boolean;
  /** Título da seção; com as duas seções, a aba já diz "Marmitas". */
  title?: string;
}

function initialSides(menu: PublicMenu): string[] {
  return menu.sideDishes.filter((d) => d.isAvailable).map((d) => d.productId);
}

/** Monta uma marmita: tamanho, prato(s), acompanhamentos, adicionais, quantidade e observação (RN-203.3). */
export function MarmitaBuilder({ menu, limits, readOnly, onAdd, title = 'Monte sua marmita' }: Props) {
  const ids = useId();
  const firstSize = menu.marmitaSizes[0]?.id ?? '';
  const [sizeId, setSizeId] = useState(firstSize);
  const [mainIds, setMainIds] = useState<string[]>([]);
  const [sideIds, setSideIds] = useState<string[]>(() => initialSides(menu));
  const [addons, setAddons] = useState<Record<string, number>>({});
  const [quantity, setQuantity] = useState(1);
  const [notes, setNotes] = useState('');
  const [announcement, setAnnouncement] = useState('');
  const [showMissingMain, setShowMissingMain] = useState(false);

  // Algo acabou desde a escolha: tira da montagem em andamento.
  const availableMain = new Set(menu.mainDishes.filter((d) => d.isAvailable).map((d) => d.productId));
  const availableSide = new Set(menu.sideDishes.filter((d) => d.isAvailable).map((d) => d.productId));
  const availableAddon = new Set(menu.addons.filter((d) => d.isAvailable).map((d) => d.productId));
  const effectiveSize = menu.marmitaSizes.some((s) => s.id === sizeId) ? sizeId : firstSize;
  const effectiveMain = mainIds.filter((id) => availableMain.has(id));
  const effectiveSide = sideIds.filter((id) => availableSide.has(id));

  const line: MarmitaLine = {
      kind: 'MARMITA',
      id: 'draft',
      sizeId: effectiveSize,
      mainDishIds: effectiveMain,
      sideDishIds: effectiveSide,
      addons: Object.entries(addons)
        .filter(([id, q]) => q > 0 && availableAddon.has(id))
        .map(([productId, q]) => ({ productId, quantity: q })),
      quantity,
      notes,
  };
  const preview = previewLine(line, menu);

  const toggle = (list: string[], id: string, on: boolean) => (on ? [...list, id] : list.filter((x) => x !== id));

  function handleAdd() {
    if (readOnly) return;
    if (effectiveMain.length === 0) {
      setShowMissingMain(true);
      document.getElementById(`${ids}-main`)?.focus();
      return;
    }
    if (!onAdd({ ...line, id: newLineId(), notes: notes.trim() })) return;
    setAnnouncement(`${quantity > 1 ? `${quantity} marmitas adicionadas` : 'Marmita adicionada'} ao pedido.`);
    setMainIds([]);
    setSideIds(initialSides(menu));
    setAddons({});
    setQuantity(1);
    setNotes('');
    setShowMissingMain(false);
  }

  if (menu.marmitaSizes.length === 0 || menu.mainDishes.length === 0) {
    return (
      <section className="card" aria-labelledby={`${ids}-title`}>
        <h2 id={`${ids}-title`}>Marmitas</h2>
        <p className="muted">Nenhuma marmita disponível hoje.</p>
      </section>
    );
  }

  return (
    <section className="card" aria-labelledby={`${ids}-title`}>
      <h2 id={`${ids}-title`}>{title}</h2>

      <fieldset disabled={readOnly}>
        <legend>Tamanho</legend>
        {menu.marmitaSizes.map((size) => (
          <label key={size.id} className="option">
            <input
              type="radio"
              name={`${ids}-size`}
              value={size.id}
              checked={effectiveSize === size.id}
              onChange={() => setSizeId(size.id)}
            />
            <span className="option-body">Marmita {size.name}</span>
            <span className="price">{formatApiMoney(size.price)}</span>
          </label>
        ))}
      </fieldset>

      <fieldset disabled={readOnly} aria-describedby={showMissingMain ? `${ids}-main-error` : undefined}>
        <legend id={`${ids}-main`} tabIndex={-1}>
          Prato principal
        </legend>
        {showMissingMain && effectiveMain.length === 0 && (
          <p id={`${ids}-main-error`} className="field-error" role="alert">
            Escolha pelo menos 1 prato.
          </p>
        )}
        {menu.mainDishes.map((dish) => (
          <label key={dish.productId} className="option">
            <input
              type="checkbox"
              checked={effectiveMain.includes(dish.productId)}
              disabled={!dish.isAvailable}
              onChange={(e) => setMainIds((l) => toggle(l, dish.productId, e.target.checked))}
            />
            {dish.imageUrl && <img className="dish-img" src={dish.imageUrl} alt="" loading="lazy" width={56} height={56} />}
            <span className="option-body">
              {dish.name}
              {dish.description && <span className="option-desc">{dish.description}</span>}
            </span>
            {!dish.isAvailable && <span className="badge">Acabou</span>}
          </label>
        ))}
      </fieldset>

      {menu.sideDishes.length > 0 && (
        <fieldset disabled={readOnly}>
          <legend>Acompanhamentos</legend>
          {menu.sideDishes.map((dish) => (
            <label key={dish.productId} className="option">
              <input
                type="checkbox"
                checked={effectiveSide.includes(dish.productId)}
                disabled={!dish.isAvailable}
                onChange={(e) => setSideIds((l) => toggle(l, dish.productId, e.target.checked))}
              />
              <span className="option-body">
                {dish.name}
                {dish.description && <span className="option-desc">{dish.description}</span>}
              </span>
              {!dish.isAvailable && <span className="badge">Acabou</span>}
            </label>
          ))}
        </fieldset>
      )}

      {menu.addons.length > 0 && (
        <fieldset disabled={readOnly}>
          <legend>Adicionais (por marmita)</legend>
          <ul className="list">
            {menu.addons.map((addon) => (
              <li key={addon.productId} className="row-between">
                <span>
                  {addon.name} <span className="price">+ {formatApiMoney(addon.price)}</span>
                  {addon.description && <span className="option-desc">{addon.description}</span>}
                </span>
                {addon.isAvailable ? (
                  <Stepper
                    label={addon.name}
                    value={addons[addon.productId] ?? 0}
                    max={limits.maxQuantityPerLine}
                    onChange={(v) => setAddons((a) => ({ ...a, [addon.productId]: Math.max(0, v) }))}
                  />
                ) : (
                  <span className="badge">Acabou</span>
                )}
              </li>
            ))}
          </ul>
        </fieldset>
      )}

      <label className="field">
        <span className="field-label">Observação da marmita (opcional)</span>
        <textarea
          className="textarea"
          maxLength={limits.notesMaxLength}
          value={notes}
          onChange={(e) => setNotes(e.target.value)}
          disabled={readOnly}
          placeholder="Ex.: sem cebola"
        />
        <span className="field-hint">
          {notes.length}/{limits.notesMaxLength}
        </span>
      </label>

      <div className="row-between">
        <span className="field-label">Quantidade</span>
        <Stepper label="marmitas" value={quantity} min={1} max={limits.maxQuantityPerLine} onChange={setQuantity} />
      </div>

      <div style={{ marginTop: '1rem' }}>
        <button type="button" className="btn btn-primary btn-block" onClick={handleAdd} disabled={readOnly}>
          Adicionar ao pedido · <span className="price">{formatBRL(preview.lineCents)}</span>
        </button>
      </div>
      <p className="visually-hidden" aria-live="polite">
        {announcement}
      </p>
    </section>
  );
}
