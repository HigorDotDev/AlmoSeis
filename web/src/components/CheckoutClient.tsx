'use client';

import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { useEffect, useRef, useState } from 'react';
import { browserApi } from '@/lib/api/client';
import type { DeliveryAddressInput, DeliveryQuote, ExpectedPaymentMethod, PublicStore, RecalculatedCart } from '@/lib/api/types';
import {
  applyCartChanged,
  describeIssue,
  previewCart,
  removeLine,
  setQuantity,
  validateCart,
  type CartIssue,
} from '@/lib/cart';
import {
  buildOrderRequest,
  EMPTY_ADDRESS,
  interpretSubmitFailure,
  validateCheckout,
  type CheckoutForm,
  type FieldErrors,
} from '@/lib/checkout';
import { useCart, useLiveStore } from '@/lib/hooks';
import { keyForAttempt } from '@/lib/idempotency';
import { formatApiMoney, formatBRL, parseMoney } from '@/lib/money';
import { maskPhone } from '@/lib/phone';
import {
  clearCustomer,
  loadCart,
  loadCustomer,
  loadPendingSubmission,
  saveCart,
  saveCustomer,
  saveOrder,
  savePendingSubmission,
} from '@/lib/storage';
import { PAYMENT_TEXT, STORE_STATUS_TEXT, formatAddress } from '@/lib/texts';
import { PageFooter } from './PageFooter';
import { PrivacyNotice } from './PrivacyNotice';
import { Stepper } from './Stepper';
import { Turnstile, type TurnstileHandle } from './Turnstile';

const TURNSTILE_SITE_KEY = process.env.NEXT_PUBLIC_TURNSTILE_SITE_KEY ?? '';
const PAYMENT_METHODS: ExpectedPaymentMethod[] = ['CASH', 'PIX', 'CARD'];

const INITIAL_FORM: CheckoutForm = {
  name: '',
  phone: '',
  fulfillmentType: 'PICKUP',
  address: EMPTY_ADDRESS,
  deliveryQuoteId: null,
  paymentMethod: null,
  changeFor: '',
  notes: '',
  website: '',
};

function FieldError({ id, message }: { id: string; message: string | undefined }) {
  if (!message) return null;
  return (
    <span id={id} className="field-error">
      {message}
    </span>
  );
}

export function CheckoutClient({ initialStore }: { initialStore: PublicStore }) {
  const router = useRouter();
  const { store, refresh } = useLiveStore(initialStore);
  const slug = store.slug;
  const { cart, update, hydrated } = useCart(slug);

  const [form, setForm] = useState<CheckoutForm>(INITIAL_FORM);
  const [remember, setRemember] = useState(true);
  const [errors, setErrors] = useState<FieldErrors>({});
  const [lineErrors, setLineErrors] = useState<Record<number, string>>({});
  const [message, setMessage] = useState<{ tone: 'danger' | 'warning' | 'success'; text: string } | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [cartChanged, setCartChanged] = useState<RecalculatedCart | null>(null);
  const [retryUntil, setRetryUntil] = useState<number | null>(null);
  const [now, setNow] = useState(() => Date.now());
  const [turnstileToken, setTurnstileToken] = useState<string | null>(null);
  const [quote, setQuote] = useState<DeliveryQuote | null>(null);
  const [quoting, setQuoting] = useState(false);
  const turnstile = useRef<TurnstileHandle>(null);
  const messageRef = useRef<HTMLDivElement>(null);

  const captchaEnabled = Boolean(TURNSTILE_SITE_KEY) && (store.privacy?.thirdParties ?? []).includes('ANTI_BOT');

  // Dados lembrados neste aparelho (ADR-0052 item 6).
  useEffect(() => {
    const saved = loadCustomer(slug);
    if (saved) {
      setForm((f) => ({
        ...f,
        name: saved.name,
        phone: maskPhone(saved.phone),
        address: saved.address ?? EMPTY_ADDRESS,
        paymentMethod: saved.paymentMethod,
      }));
    }
  }, [slug]);

  // Contagem regressiva do 429.
  useEffect(() => {
    if (!retryUntil) return;
    const t = setInterval(() => {
      const n = Date.now();
      setNow(n);
      if (n >= retryUntil) setRetryUntil(null);
    }, 1000);
    return () => clearInterval(t);
  }, [retryUntil]);

  useEffect(() => {
    if (message) messageRef.current?.focus();
  }, [message]);

  const menu = store.menu;
  const deliveryFeeCents = form.fulfillmentType === 'DELIVERY' && quote ? parseMoney(quote.deliveryFee) : 0;
  const preview = menu ? previewCart(cart, menu, deliveryFeeCents) : null;
  const cartIssues: CartIssue[] = menu ? validateCart(cart, menu, store.limits) : [];
  const globalIssues = cartIssues.filter((i) => !('lineId' in i));
  const issuesByLine = new Map<string, CartIssue[]>();
  for (const issue of cartIssues) {
    if ('lineId' in issue) issuesByLine.set(issue.lineId, [...(issuesByLine.get(issue.lineId) ?? []), issue]);
  }
  const secondsLeft = retryUntil ? Math.max(0, Math.ceil((retryUntil - now) / 1000)) : 0;
  const isOpen = store.status === 'OPEN' && Boolean(menu);
  const waitingCaptcha = captchaEnabled && !turnstileToken;

  function setField<K extends keyof CheckoutForm>(key: K, value: CheckoutForm[K]) {
    setForm((f) => ({ ...f, [key]: value }));
    setErrors((e) => ({ ...e, [key === 'name' ? 'customerName' : key === 'phone' ? 'customerPhone' : key]: undefined }));
  }

  function setAddress<K extends keyof DeliveryAddressInput>(key: K, value: string) {
    setForm((f) => ({ ...f, address: { ...f.address, [key]: value }, deliveryQuoteId: null }));
    setQuote(null);
    setErrors((e) => ({ ...e, [`deliveryAddress.${key}`]: undefined }));
  }

  async function handleQuote() {
    const addr = form.address;
    if (!addr.street.trim() || !addr.number.trim() || !addr.neighborhood.trim() || !addr.city.trim()) {
      setErrors((e) => ({ ...e, deliveryQuoteId: 'Preencha rua, número, bairro e cidade para calcular a taxa.' }));
      return;
    }
    setQuoting(true);
    const result = await browserApi().quoteDelivery(slug, addr);
    setQuoting(false);
    if (result.ok) {
      setQuote(result.data);
      setForm((f) => ({ ...f, deliveryQuoteId: result.data.quoteId }));
      setErrors((e) => ({ ...e, deliveryQuoteId: undefined }));
      return;
    }
    const msg =
      result.kind === 'problem' && result.problem.errorCode === 'OutOfDeliveryArea'
        ? 'Não entregamos nesse endereço. Você pode retirar no restaurante.'
        : 'Não foi possível calcular a entrega agora. Você pode retirar no restaurante.';
    setErrors((e) => ({ ...e, deliveryQuoteId: msg }));
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (submitting || !menu || !preview) return;
    setMessage(null);
    setLineErrors({});
    const localErrors = validateCheckout(form, preview, store.limits.notesMaxLength);
    setErrors(localErrors);
    if (Object.keys(localErrors).length > 0 || cartIssues.length > 0) {
      setMessage({ tone: 'danger', text: 'Confira os campos destacados.' });
      return;
    }
    if (waitingCaptcha) {
      setMessage({ tone: 'warning', text: 'Aguarde a verificação anti-robô terminar.' });
      return;
    }

    const body = buildOrderRequest(form, cart, preview);
    const bodyJson = JSON.stringify(body);
    // Mesma chave em todo reenvio do mesmo corpo, inclusive depois de recarregar a página (§6.19).
    const attempt = keyForAttempt(bodyJson, loadPendingSubmission(slug));
    savePendingSubmission(slug, attempt);

    setSubmitting(true);
    const result = await browserApi().placeOrder(slug, body, {
      idempotencyKey: attempt.idempotencyKey,
      turnstileToken: captchaEnabled ? turnstileToken : null,
    });

    if (result.ok) {
      const placed = result.data;
      saveOrder(slug, {
        trackingToken: placed.trackingToken,
        submittedAt: new Date().toISOString(),
        trackingExpiresAt: placed.trackingExpiresAt,
        totalAmount: placed.totalAmount,
        cart: loadCart(slug),
      });
      if (remember) {
        saveCustomer(slug, {
          name: form.name.trim(),
          phone: form.phone,
          address: form.fulfillmentType === 'DELIVERY' ? form.address : (loadCustomer(slug)?.address ?? null),
          paymentMethod: form.paymentMethod,
        });
      } else {
        clearCustomer(slug);
      }
      savePendingSubmission(slug, null);
      saveCart(slug, { lines: [] });
      router.push(`/${slug}/pedido/${placed.trackingToken}?enviado=1`);
      return;
    }

    setSubmitting(false);
    // O token do Turnstile é de uso único: depois de qualquer erro, pede outro e reenvia com a mesma chave.
    turnstile.current?.reset();
    const outcome = interpretSubmitFailure(result);
    switch (outcome.kind) {
      case 'cartChanged':
        setCartChanged(outcome.cart);
        setMessage({ tone: 'warning', text: outcome.message });
        void refresh();
        break;
      case 'fieldErrors': {
        const items: Record<number, string> = {};
        const fields: FieldErrors = {};
        for (const [k, v] of Object.entries(outcome.errors)) {
          const m = /^items\[(\d+)\]/.exec(k);
          if (m) items[Number(m[1])] = [items[Number(m[1])], v].filter(Boolean).join(' ');
          else fields[k] = v;
        }
        setErrors(fields);
        setLineErrors(items);
        setMessage({ tone: 'danger', text: outcome.message });
        break;
      }
      case 'storeUnavailable':
        setMessage({ tone: 'warning', text: outcome.message });
        void refresh();
        break;
      case 'rateLimited':
        setRetryUntil(Date.now() + outcome.retryAfterSeconds * 1000);
        setNow(Date.now());
        setMessage({ tone: 'warning', text: outcome.message });
        break;
      case 'captcha':
      case 'tooManyPending':
      case 'network':
      case 'message':
        if (result.kind === 'problem' && result.problem.errorCode === 'IdempotencyKeyReused') savePendingSubmission(slug, null);
        setMessage({ tone: outcome.kind === 'network' ? 'warning' : 'danger', text: outcome.message });
        break;
    }
  }

  async function confirmCartChanged() {
    if (!cartChanged) return;
    update((c) => applyCartChanged(c, cartChanged));
    setCartChanged(null);
    await refresh();
    setMessage({ tone: 'success', text: 'Pedido atualizado. Confira os valores e toque em "Enviar pedido".' });
  }

  if (!hydrated) {
    return (
      <main id="conteudo" className="page">
        <p className="muted">Carregando seu pedido…</p>
      </main>
    );
  }

  if (cart.lines.length === 0) {
    return (
      <main id="conteudo" className="page">
        <h1>Seu pedido</h1>
        <p>Seu pedido está vazio.</p>
        <Link href={`/${slug}`} className="btn btn-primary">
          Ver o cardápio
        </Link>
        <PageFooter />
      </main>
    );
  }

  const errId = (k: string) => `err-${k.replace(/[^\w]/g, '-')}`;
  const invalid = (k: string) => (errors[k] ? { 'aria-invalid': true as const, 'aria-describedby': errId(k) } : {});

  return (
    <main id="conteudo" className="page">
      <p>
        <Link href={`/${slug}`}>← Voltar ao cardápio</Link>
      </p>
      <h1>Seu pedido</h1>
      <p className="muted small">{store.name}</p>

      {store.status !== 'OPEN' && (
        <div className="notice notice-warning" role="status">
          <strong>{STORE_STATUS_TEXT[store.status].title}</strong>
          <p>{STORE_STATUS_TEXT[store.status].body}</p>
        </div>
      )}
      {store.status === 'OPEN' && !menu && (
        <div className="notice notice-warning" role="status">
          O cardápio de hoje ainda não foi publicado.
        </div>
      )}

      <div ref={messageRef} tabIndex={-1} aria-live="assertive">
        {message && (
          <div className={`notice notice-${message.tone}`} role={message.tone === 'danger' ? 'alert' : 'status'}>
            {message.text}
            {secondsLeft > 0 && <p>Você poderá tentar de novo em {secondsLeft} s.</p>}
          </div>
        )}
      </div>

      {cartChanged && (
        <section className="card" aria-labelledby="cart-changed-title">
          <h2 id="cart-changed-title">Seu pedido mudou</h2>
          <p>Confira os itens e os valores atualizados pelo restaurante:</p>
          <ul className="list">
            {cartChanged.items.map((item) => (
              <li key={item.index} className="row-between">
                <span>
                  {item.name}
                  {!item.available && (
                    <>
                      {' '}
                      <span className="badge">Acabou</span>
                    </>
                  )}
                </span>
                <span className="price">{item.available ? formatApiMoney(item.lineTotal) : 'sai do pedido'}</span>
              </li>
            ))}
          </ul>
          <p className="row-between">
            <strong>Novo total</strong>
            <span className="price">{formatApiMoney(cartChanged.totalAmount)}</span>
          </p>
          <div className="stack">
            <button type="button" className="btn btn-primary btn-block" onClick={confirmCartChanged}>
              Aceitar as mudanças
            </button>
            <Link href={`/${slug}`} className="btn btn-block">
              Mudar meu pedido
            </Link>
          </div>
        </section>
      )}

      <section className="card" aria-labelledby="itens-title">
        <h2 id="itens-title">Itens</h2>
        {globalIssues.map((issue) => (
          <p key={issue.code} className="field-error">
            {describeIssue(issue)}
          </p>
        ))}
        <ul className="list">
          {cart.lines.map((line, index) => {
            const lp = preview?.lines[index];
            const issues = issuesByLine.get(line.id) ?? [];
            const title = lp?.title ?? 'Item';
            return (
              <li key={line.id}>
                <div className="row-between">
                  <strong>
                    {line.quantity}× {title}
                  </strong>
                  <span className="price">{lp ? formatBRL(lp.lineCents) : '—'}</span>
                </div>
                {lp?.details.map((d, i) => (
                  <p key={i} className="muted small">
                    {d}
                  </p>
                ))}
                {issues.map((issue) => (
                  <p key={issue.code} className="field-error">
                    {describeIssue(issue)}
                  </p>
                ))}
                {lineErrors[index] && <p className="field-error">{lineErrors[index]}</p>}
                <div className="row-between">
                  <Stepper
                    label={title}
                    value={line.quantity}
                    min={1}
                    max={store.limits.maxQuantityPerLine}
                    onChange={(q) => update((c) => setQuantity(c, line.id, q, store.limits.maxQuantityPerLine))}
                  />
                  <button type="button" className="btn-link" onClick={() => update((c) => removeLine(c, line.id))}>
                    Remover <span className="visually-hidden">{title}</span>
                  </button>
                </div>
              </li>
            );
          })}
        </ul>
        <p>
          <Link href={`/${slug}`}>+ Adicionar mais itens</Link>
        </p>
      </section>

      <form onSubmit={handleSubmit} noValidate>
        <section className="card" aria-labelledby="dados-title">
          <h2 id="dados-title">Seus dados</h2>
          <label className="field">
            <span className="field-label">Nome</span>
            <input
              className="input"
              name="name"
              autoComplete="name"
              maxLength={80}
              value={form.name}
              onChange={(e) => setField('name', e.target.value)}
              {...invalid('customerName')}
            />
            <FieldError id={errId('customerName')} message={errors.customerName} />
          </label>
          <label className="field">
            <span className="field-label">Telefone (WhatsApp)</span>
            <input
              className="input"
              name="phone"
              type="tel"
              inputMode="tel"
              autoComplete="tel-national"
              placeholder="(34) 98888-7777"
              value={form.phone}
              onChange={(e) => setField('phone', maskPhone(e.target.value))}
              {...invalid('customerPhone')}
            />
            <span className="field-hint">O restaurante usa para falar com você sobre o pedido.</span>
            <FieldError id={errId('customerPhone')} message={errors.customerPhone} />
          </label>
          <label className="option">
            <input type="checkbox" checked={remember} onChange={(e) => setRemember(e.target.checked)} />
            <span className="option-body">Lembrar meus dados neste aparelho</span>
          </label>
        </section>

        <section className="card" aria-labelledby="receber-title">
          <fieldset aria-describedby={errors.fulfillmentType ? errId('fulfillmentType') : undefined}>
            <legend id="receber-title">Como você quer receber</legend>
            <label className="option">
              <input
                type="radio"
                name="fulfillment"
                checked={form.fulfillmentType === 'PICKUP'}
                disabled={!store.pickupAvailable}
                onChange={() => setField('fulfillmentType', 'PICKUP')}
              />
              <span className="option-body">
                Retirar no restaurante
                {store.address && <span className="option-desc">{formatAddress(store.address)}</span>}
              </span>
            </label>
            <label className="option">
              <input
                type="radio"
                name="fulfillment"
                checked={form.fulfillmentType === 'DELIVERY'}
                disabled={!store.deliveryAvailable}
                onChange={() => setField('fulfillmentType', 'DELIVERY')}
              />
              <span className="option-body">
                Entrega
                <span className="option-desc">
                  {store.deliveryAvailable && store.deliveryRule
                    ? `${formatApiMoney(store.deliveryRule.pricePerKm)}/km, mínimo ${formatApiMoney(store.deliveryRule.minimumFee)}${
                        store.deliveryRule.maxDistanceKm ? `, até ${store.deliveryRule.maxDistanceKm.replace('.', ',')} km` : ''
                      }`
                    : 'Em breve'}
                </span>
              </span>
            </label>
            <FieldError id={errId('fulfillmentType')} message={errors.fulfillmentType} />
          </fieldset>

          {form.fulfillmentType === 'DELIVERY' && store.deliveryAvailable && (
            <fieldset>
              <legend>Endereço de entrega</legend>
              {(
                [
                  ['street', 'Rua', 'address-line1'],
                  ['number', 'Número', 'off'],
                  ['complement', 'Complemento (opcional)', 'address-line2'],
                  ['neighborhood', 'Bairro', 'address-level3'],
                  ['city', 'Cidade', 'address-level2'],
                  ['zipCode', 'CEP (opcional)', 'postal-code'],
                  ['reference', 'Ponto de referência (opcional)', 'off'],
                ] as const
              ).map(([k, label, ac]) => (
                <label className="field" key={k}>
                  <span className="field-label">{label}</span>
                  <input
                    className="input"
                    autoComplete={ac}
                    inputMode={k === 'zipCode' ? 'numeric' : undefined}
                    value={form.address[k] ?? ''}
                    onChange={(e) => setAddress(k, e.target.value)}
                    {...invalid(`deliveryAddress.${k}`)}
                  />
                  <FieldError id={errId(`deliveryAddress.${k}`)} message={errors[`deliveryAddress.${k}`]} />
                </label>
              ))}
              {quote ? (
                <div className="notice notice-success">
                  <p>
                    Entendemos: <strong>{quote.formattedAddress}</strong>
                  </p>
                  <p>
                    {quote.distanceKm.replace('.', ',')} km · taxa <span className="price">{formatApiMoney(quote.deliveryFee)}</span>
                  </p>
                </div>
              ) : (
                <button type="button" className="btn btn-block" onClick={handleQuote} disabled={quoting}>
                  {quoting ? 'Calculando…' : 'Calcular taxa de entrega'}
                </button>
              )}
              <FieldError id={errId('deliveryQuoteId')} message={errors.deliveryQuoteId} />
            </fieldset>
          )}
        </section>

        <section className="card" aria-labelledby="pagamento-title">
          <fieldset aria-describedby={errors.expectedPaymentMethod ? errId('expectedPaymentMethod') : undefined}>
            <legend id="pagamento-title">Pagamento na {form.fulfillmentType === 'DELIVERY' ? 'entrega' : 'retirada'}</legend>
            {PAYMENT_METHODS.map((m) => (
              <label className="option" key={m}>
                <input
                  type="radio"
                  name="payment"
                  checked={form.paymentMethod === m}
                  onChange={() => setField('paymentMethod', m)}
                />
                <span className="option-body">{PAYMENT_TEXT[m]}</span>
              </label>
            ))}
            <FieldError id={errId('expectedPaymentMethod')} message={errors.expectedPaymentMethod} />
          </fieldset>
          {form.paymentMethod === 'CASH' && (
            <label className="field">
              <span className="field-label">Troco para quanto? (opcional)</span>
              <input
                className="input"
                inputMode="decimal"
                placeholder="Ex.: 50,00"
                value={form.changeFor}
                onChange={(e) => setField('changeFor', e.target.value)}
                {...invalid('changeFor')}
              />
              <span className="field-hint">Deixe em branco se não precisar de troco.</span>
              <FieldError id={errId('changeFor')} message={errors.changeFor} />
            </label>
          )}
          <p className="muted small">O pagamento é feito no restaurante; nada é cobrado pela página.</p>
        </section>

        <section className="card" aria-labelledby="obs-title">
          <label className="field">
            <span className="field-label" id="obs-title">
              Observações do pedido (opcional)
            </span>
            <textarea
              className="textarea"
              maxLength={store.limits.notesMaxLength}
              value={form.notes}
              onChange={(e) => setField('notes', e.target.value)}
              placeholder="Ex.: sem talher"
              {...invalid('notes')}
            />
            <span className="field-hint">
              {form.notes.length}/{store.limits.notesMaxLength}
            </span>
            <FieldError id={errId('notes')} message={errors.notes} />
          </label>
        </section>

        {/* Campo-isca (ADR-0045): invisível para gente e para leitor de tela. */}
        <div className="honeypot" aria-hidden="true">
          <label>
            Site
            <input
              type="text"
              name="website"
              tabIndex={-1}
              autoComplete="off"
              value={form.website}
              onChange={(e) => setField('website', e.target.value)}
            />
          </label>
        </div>

        {preview && (
          <section className="card" aria-labelledby="total-title">
            <h2 id="total-title" className="visually-hidden">
              Total
            </h2>
            <p className="row-between">
              <span>Itens</span>
              <span className="price">{formatBRL(preview.itemsCents)}</span>
            </p>
            {form.fulfillmentType === 'DELIVERY' && (
              <p className="row-between">
                <span>Taxa de entrega</span>
                <span className="price">{quote ? formatBRL(preview.deliveryFeeCents) : 'a calcular'}</span>
              </p>
            )}
            <p className="row-between">
              <strong>Total</strong>
              <strong className="price">{formatBRL(preview.totalCents)}</strong>
            </p>
            <p className="muted small">O restaurante confirma o valor ao aceitar o pedido.</p>
          </section>
        )}

        <PrivacyNotice privacy={store.privacy} storeName={store.name} />

        {captchaEnabled && isOpen && (
          <div style={{ marginBottom: '1rem' }}>
            <Turnstile
              ref={turnstile}
              siteKey={TURNSTILE_SITE_KEY}
              onToken={setTurnstileToken}
              onError={(text) => setMessage({ tone: 'warning', text })}
            />
          </div>
        )}

        <button
          type="submit"
          className="btn btn-primary btn-block"
          disabled={!isOpen || submitting || secondsLeft > 0 || Boolean(cartChanged)}
          aria-describedby={waitingCaptcha ? 'captcha-hint' : undefined}
        >
          {submitting ? 'Enviando…' : secondsLeft > 0 ? `Aguarde ${secondsLeft} s` : 'Enviar pedido'}
        </button>
        {waitingCaptcha && isOpen && (
          <p id="captcha-hint" className="muted small">
            Aguardando a verificação anti-robô…
          </p>
        )}
      </form>

      <PageFooter />
    </main>
  );
}
