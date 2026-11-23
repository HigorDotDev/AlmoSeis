'use client';

import { useId, useState } from 'react';
import type { PublicAddonGroup, PublicBeverageType, PublicCombo, PublicFixedDish, PublicFixedMenu, PublicLimits } from '@/lib/api/types';
import { newLineId, previewLine, type FixedLine } from '@/lib/cart';
import {
  addonGroupsFor,
  beverageLineFromDraft,
  buildBeverageLine,
  buildComboLine,
  buildDishLine,
  comboLineFromDraft,
  dishLineFromDraft,
  emptyBeverageDraft,
  emptyComboDraft,
  emptyDishDraft,
  indexFixedMenu,
  MAX_ADDON_OPTIONS_PER_DISH,
  type ComboPartDraft,
  type SelectionErrors,
} from '@/lib/fixed-menu';
import { formatApiMoney, formatBRL, parseMoney } from '@/lib/money';
import type { FixedSelection } from './FixedMenuPanel';
import { Stepper } from './Stepper';

interface Props {
  selection: FixedSelection;
  fixedMenu: PublicFixedMenu;
  limits: PublicLimits;
  readOnly: boolean;
  /** Id do título (o `<dialog>` usa no `aria-labelledby`). */
  titleId: string;
  onAdd: (line: FixedLine, announcement: string) => void;
  onClose: () => void;
}

/**
 * Montagem de um item do cardápio fixo (RN-316, fatia 5): prato → tamanho → adicionais → observação; bebida →
 * tamanho → sabor; combo → adicionais e observação por prato, sabor por bebida. O item é relido do cardápio vivo: se
 * foi pausado enquanto o cliente escolhia, a montagem avisa.
 */
export function FixedItemBuilder({ selection, fixedMenu, titleId, onClose, ...rest }: Props) {
  if (selection.kind === 'DISH') {
    const dish = fixedMenu.categories.flatMap((c) => c.dishes).find((d) => d.id === selection.id);
    if (dish) return <DishBuilder dish={dish} fixedMenu={fixedMenu} titleId={titleId} onClose={onClose} {...rest} />;
  } else if (selection.kind === 'BEVERAGE') {
    const type = fixedMenu.beverages.find((b) => b.id === selection.id);
    if (type && type.sizes.length) return <BeverageBuilder type={type} titleId={titleId} onClose={onClose} {...rest} />;
  } else {
    const combo = fixedMenu.combos.find((c) => c.id === selection.id);
    if (combo) return <ComboBuilder combo={combo} fixedMenu={fixedMenu} titleId={titleId} onClose={onClose} {...rest} />;
  }
  return (
    <div>
      <h2 id={titleId}>Item indisponível</h2>
      <p>Este item acabou de sair do cardápio. Escolha outro.</p>
      <button type="button" className="btn btn-block" onClick={onClose}>
        Voltar ao cardápio
      </button>
    </div>
  );
}

type BuilderProps = Omit<Props, 'selection' | 'fixedMenu'>;

const focusFirstError = (ids: string[]) => {
  for (const id of ids) {
    const el = document.getElementById(id);
    if (el) {
      el.focus();
      return;
    }
  }
};

function FieldError({ id, message }: { id: string; message: string | undefined }) {
  if (!message) return null;
  return (
    <p id={id} className="field-error" role="alert">
      {message}
    </p>
  );
}

function Header({ titleId, name, description, imageUrl }: { titleId: string; name: string; description: string | null; imageUrl: string | null }) {
  return (
    <div className="row" style={{ marginBottom: '0.75rem' }}>
      {imageUrl && <img className="dish-img" src={imageUrl} alt="" width={56} height={56} />}
      <div>
        <h2 id={titleId}>{name}</h2>
        {description && <p className="muted small">{description}</p>}
      </div>
    </div>
  );
}

/** Grupos de adicionais (1 a `maxAddonQuantity` cada, até 10 opções por prato). */
function AddonGroupsField({
  groups,
  value,
  onChange,
  max,
  disabled,
  idBase,
  error,
  forName,
}: {
  groups: PublicAddonGroup[];
  value: Record<string, number>;
  onChange: (value: Record<string, number>) => void;
  max: number;
  disabled: boolean;
  idBase: string;
  error: string | undefined;
  /** "(X-Burguer)", no combo. */
  forName?: string;
}) {
  const chosen = Object.values(value).filter((q) => q > 0).length;
  return (
    <>
      {groups.map((group, gi) => (
        <fieldset key={group.id} disabled={disabled} aria-describedby={error && gi === 0 ? `${idBase}-error` : undefined}>
          <legend id={gi === 0 ? `${idBase}-legend` : undefined} tabIndex={gi === 0 ? -1 : undefined}>
            {group.name}
            {forName ? ` ${forName}` : ''} <span className="muted small">(opcional)</span>
          </legend>
          {gi === 0 && <FieldError id={`${idBase}-error`} message={error} />}
          <ul className="list">
            {group.options.map((option) => {
              const q = value[option.id] ?? 0;
              return (
                <li key={option.id} className="row-between">
                  <span>
                    {option.name} <span className="price">+ {formatApiMoney(option.price)}</span>
                  </span>
                  <Stepper
                    label={option.name}
                    value={q}
                    max={q === 0 && chosen >= MAX_ADDON_OPTIONS_PER_DISH ? 0 : max}
                    onChange={(v) => onChange({ ...value, [option.id]: Math.max(0, Math.min(max, v)) })}
                  />
                </li>
              );
            })}
          </ul>
        </fieldset>
      ))}
    </>
  );
}

function NotesField({
  label,
  value,
  onChange,
  max,
  disabled,
  id,
  error,
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  max: number;
  disabled: boolean;
  id: string;
  error: string | undefined;
}) {
  return (
    <label className="field">
      <span className="field-label">{label}</span>
      <textarea
        id={id}
        className="textarea"
        maxLength={max}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        disabled={disabled}
        placeholder="Ex.: sem cebola"
        aria-invalid={error ? true : undefined}
        aria-describedby={error ? `${id}-error` : undefined}
      />
      <span className="field-hint">
        {value.length}/{max}
      </span>
      <FieldError id={`${id}-error`} message={error} />
    </label>
  );
}

function Footer({
  quantity,
  setQuantity,
  limits,
  quantityLabel,
  priceCents,
  readOnly,
  onAdd,
  onClose,
}: {
  quantity: number;
  setQuantity: (q: number) => void;
  limits: PublicLimits;
  quantityLabel: string;
  priceCents: number | null;
  readOnly: boolean;
  onAdd: () => void;
  onClose: () => void;
}) {
  return (
    <>
      <div className="row-between" style={{ marginBottom: '1rem' }}>
        <span className="field-label">Quantidade</span>
        <Stepper label={quantityLabel} value={quantity} min={1} max={limits.maxQuantityPerLine} onChange={setQuantity} />
      </div>
      <div className="stack">
        <button type="button" className="btn btn-primary btn-block" onClick={onAdd} disabled={readOnly}>
          Adicionar ao pedido{priceCents != null && <> · <span className="price">{formatBRL(priceCents)}</span></>}
        </button>
        <button type="button" className="btn btn-block" onClick={onClose}>
          Cancelar
        </button>
      </div>
    </>
  );
}

function DishBuilder({ dish, fixedMenu, limits, readOnly, titleId, onAdd, onClose }: BuilderProps & { dish: PublicFixedDish; fixedMenu: PublicFixedMenu }) {
  const ids = useId();
  const index = indexFixedMenu(fixedMenu);
  const [draft, setDraft] = useState(() => emptyDishDraft(dish));
  const [errors, setErrors] = useState<SelectionErrors>({});
  const groups = addonGroupsFor(dish.addonGroupIds, index);
  const preview = previewLine(dishLineFromDraft(dish, draft, 'draft'), { menu: null, fixedMenu });

  function add() {
    const result = buildDishLine(dish, draft, index, limits, newLineId());
    if (!result.ok) {
      setErrors(result.errors);
      focusFirstError(
        [result.errors.size && `${ids}-size`, result.errors.addons && `${ids}-addons-legend`, result.errors.notes && `${ids}-notes`].filter(
          (x): x is string => Boolean(x),
        ),
      );
      return;
    }
    const name = preview.title;
    onAdd(result.line, `Adicionado ao pedido: ${draft.quantity}× ${name}.`);
  }

  return (
    <div>
      <Header titleId={titleId} name={dish.name} description={dish.description} imageUrl={dish.imageUrl} />

      {dish.hasSizes && (
        <fieldset disabled={readOnly} aria-describedby={errors.size ? `${ids}-size-error` : undefined}>
          <legend id={`${ids}-size`} tabIndex={-1}>
            Tamanho
          </legend>
          <FieldError id={`${ids}-size-error`} message={errors.size} />
          {dish.sizes.map((size) => (
            <label key={size.id} className="option">
              <input
                type="radio"
                name={`${ids}-size`}
                checked={draft.sizeId === size.id}
                onChange={() => {
                  setDraft((d) => ({ ...d, sizeId: size.id }));
                  setErrors((e) => ({ ...e, size: undefined }));
                }}
              />
              <span className="option-body">{size.name}</span>
              <span className="price">{formatApiMoney(size.price)}</span>
            </label>
          ))}
        </fieldset>
      )}

      <AddonGroupsField
        groups={groups}
        value={draft.addons}
        onChange={(addons) => {
          setDraft((d) => ({ ...d, addons }));
          setErrors((e) => ({ ...e, addons: undefined }));
        }}
        max={limits.maxAddonQuantity}
        disabled={readOnly}
        idBase={`${ids}-addons`}
        error={errors.addons}
      />

      <NotesField
        id={`${ids}-notes`}
        label="Observação (opcional)"
        value={draft.notes}
        onChange={(notes) => setDraft((d) => ({ ...d, notes }))}
        max={limits.notesMaxLength}
        disabled={readOnly}
        error={errors.notes}
      />

      <Footer
        quantity={draft.quantity}
        setQuantity={(quantity) => setDraft((d) => ({ ...d, quantity }))}
        limits={limits}
        quantityLabel={dish.name}
        priceCents={preview.incomplete || preview.unavailable ? null : preview.lineCents}
        readOnly={readOnly}
        onAdd={add}
        onClose={onClose}
      />
    </div>
  );
}

function BeverageBuilder({ type, limits, readOnly, titleId, onAdd, onClose }: BuilderProps & { type: PublicBeverageType }) {
  const ids = useId();
  const [draft, setDraft] = useState(() => emptyBeverageDraft(type));
  const [errors, setErrors] = useState<SelectionErrors>({});
  const size = type.sizes.find((s) => s.id === draft.sizeId) ?? null;
  const draftLine = beverageLineFromDraft(type, draft, 'draft');
  const flavor = size?.flavors.find((f) => f.id === draft.flavorId) ?? null;
  const unitCents = size && (size.flavors.length === 0 || flavor) ? parseMoney(size.price) + (flavor ? parseMoney(flavor.extraPrice) : 0) : null;

  function add() {
    const result = buildBeverageLine(type, draft, limits, newLineId());
    if (!result.ok) {
      setErrors(result.errors);
      focusFirstError([result.errors.size && `${ids}-size`, result.errors.flavor && `${ids}-flavor`].filter((x): x is string => Boolean(x)));
      return;
    }
    const name = `${type.name} ${size?.name ?? ''}${flavor ? ` · ${flavor.name}` : ''}`;
    onAdd(result.line, `Adicionado ao pedido: ${draft.quantity}× ${name}.`);
  }

  return (
    <div>
      <Header titleId={titleId} name={type.name} description={null} imageUrl={null} />

      <fieldset disabled={readOnly} aria-describedby={errors.size ? `${ids}-size-error` : undefined}>
        <legend id={`${ids}-size`} tabIndex={-1}>
          Tamanho
        </legend>
        <FieldError id={`${ids}-size-error`} message={errors.size} />
        {type.sizes.map((s) => (
          <label key={s.id} className="option">
            <input
              type="radio"
              name={`${ids}-size`}
              checked={draft.sizeId === s.id}
              onChange={() => {
                setDraft((d) => ({ ...d, sizeId: s.id, flavorId: s.flavors.length === 1 ? s.flavors[0]!.id : null }));
                setErrors((e) => ({ ...e, size: undefined, flavor: undefined }));
              }}
            />
            <span className="option-body">{s.name}</span>
            <span className="price">{formatApiMoney(s.price)}</span>
          </label>
        ))}
      </fieldset>

      {size && size.flavors.length > 0 && (
        <fieldset disabled={readOnly} aria-describedby={errors.flavor ? `${ids}-flavor-error` : undefined}>
          <legend id={`${ids}-flavor`} tabIndex={-1}>
            Sabor
          </legend>
          <FieldError id={`${ids}-flavor-error`} message={errors.flavor} />
          {size.flavors.map((f) => (
            <label key={f.id} className="option">
              <input
                type="radio"
                name={`${ids}-flavor`}
                checked={draft.flavorId === f.id}
                onChange={() => {
                  setDraft((d) => ({ ...d, flavorId: f.id }));
                  setErrors((e) => ({ ...e, flavor: undefined }));
                }}
              />
              <span className="option-body">{f.name}</span>
              {parseMoney(f.extraPrice) > 0 && <span className="price">+ {formatApiMoney(f.extraPrice)}</span>}
            </label>
          ))}
        </fieldset>
      )}

      <Footer
        quantity={draft.quantity}
        setQuantity={(quantity) => setDraft((d) => ({ ...d, quantity }))}
        limits={limits}
        quantityLabel={type.name}
        priceCents={draftLine && unitCents != null ? unitCents * draft.quantity : null}
        readOnly={readOnly}
        onAdd={add}
        onClose={onClose}
      />
    </div>
  );
}

function ComboBuilder({ combo, fixedMenu, limits, readOnly, titleId, onAdd, onClose }: BuilderProps & { combo: PublicCombo; fixedMenu: PublicFixedMenu }) {
  const ids = useId();
  const index = indexFixedMenu(fixedMenu);
  const [draft, setDraft] = useState(() => emptyComboDraft(combo));
  const [errors, setErrors] = useState<SelectionErrors>({});
  const preview = previewLine(comboLineFromDraft(combo, draft, 'draft'), { menu: null, fixedMenu });

  const setPart = (componentId: string, change: Partial<ComboPartDraft>) =>
    setDraft((d) => ({
      ...d,
      parts: { ...d.parts, [componentId]: { ...(d.parts[componentId] ?? { flavorId: null, addons: {}, notes: '' }), ...change } },
    }));

  function add() {
    const result = buildComboLine(combo, draft, index, limits, newLineId());
    if (!result.ok) {
      setErrors(result.errors);
      const order = combo.components.flatMap((c) => [
        result.errors[`parts.${c.id}.flavor`] && `${ids}-${c.id}-flavor`,
        result.errors[`parts.${c.id}.addons`] && `${ids}-${c.id}-addons-legend`,
        result.errors[`parts.${c.id}.notes`] && `${ids}-${c.id}-notes`,
      ]);
      focusFirstError(order.filter((x): x is string => Boolean(x)));
      return;
    }
    onAdd(result.line, `Adicionado ao pedido: ${draft.quantity}× ${combo.name}.`);
  }

  return (
    <div>
      <Header titleId={titleId} name={combo.name} description={combo.description} imageUrl={combo.imageUrl} />
      <p className="muted small">
        Combo <span className="price">{formatApiMoney(combo.price)}</span>
      </p>

      {combo.components.map((c) => {
        const part = draft.parts[c.id] ?? { flavorId: null, addons: {}, notes: '' };
        const title = `${c.quantity > 1 ? `${c.quantity}× ` : ''}${c.name}`;
        if (c.kind === 'BEVERAGE') {
          if (c.flavors.length === 0) {
            return (
              <p key={c.id} className="option">
                <span className="option-body">{title}</span>
                <span className="muted small">Incluído</span>
              </p>
            );
          }
          const errorKey = `parts.${c.id}.flavor`;
          return (
            <fieldset key={c.id} disabled={readOnly} aria-describedby={errors[errorKey] ? `${ids}-${c.id}-flavor-error` : undefined}>
              <legend id={`${ids}-${c.id}-flavor`} tabIndex={-1}>
                Sabor: {title}
              </legend>
              <FieldError id={`${ids}-${c.id}-flavor-error`} message={errors[errorKey]} />
              {c.flavors.map((f) => (
                <label key={f.id} className="option">
                  <input
                    type="radio"
                    name={`${ids}-${c.id}-flavor`}
                    checked={part.flavorId === f.id}
                    onChange={() => {
                      setPart(c.id, { flavorId: f.id });
                      setErrors((e) => ({ ...e, [errorKey]: undefined }));
                    }}
                  />
                  <span className="option-body">{f.name}</span>
                  {parseMoney(f.extraPrice) > 0 && <span className="price">+ {formatApiMoney(f.extraPrice)}</span>}
                </label>
              ))}
            </fieldset>
          );
        }
        const groups = addonGroupsFor(c.addonGroupIds, index);
        return (
          <div key={c.id} className="combo-part">
            <h3>{title}</h3>
            <AddonGroupsField
              groups={groups}
              value={part.addons}
              onChange={(addons) => {
                setPart(c.id, { addons });
                setErrors((e) => ({ ...e, [`parts.${c.id}.addons`]: undefined }));
              }}
              max={limits.maxAddonQuantity}
              disabled={readOnly}
              idBase={`${ids}-${c.id}-addons`}
              error={errors[`parts.${c.id}.addons`]}
              forName={`(${c.name})`}
            />
            <NotesField
              id={`${ids}-${c.id}-notes`}
              label={`Observação para ${c.name} (opcional)`}
              value={part.notes}
              onChange={(notes) => setPart(c.id, { notes })}
              max={limits.notesMaxLength}
              disabled={readOnly}
              error={errors[`parts.${c.id}.notes`]}
            />
          </div>
        );
      })}

      <Footer
        quantity={draft.quantity}
        setQuantity={(quantity) => setDraft((d) => ({ ...d, quantity }))}
        limits={limits}
        quantityLabel={combo.name}
        priceCents={preview.incomplete || preview.unavailable ? null : preview.lineCents}
        readOnly={readOnly}
        onAdd={add}
        onClose={onClose}
      />
    </div>
  );
}
