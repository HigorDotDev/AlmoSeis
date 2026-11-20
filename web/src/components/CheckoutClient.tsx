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
import {
  addressFingerprint,
  describeDeliveryRule,
  describeQuote,
  interpretQuoteFailure,
  normalizeAddressInput,
  quoteState,
  validateAddress,
  type QuotedAddress,
  type QuoteState,
} from '@/lib/delivery';
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
  const [quoteChanged, setQuoteChanged] = useState<DeliveryQuote | null>(null);
  const [retryUntil, setRetryUntil] = useState<number | null>(null);
  const [quoteRetryUntil, setQuoteRetryUntil] = useState<number | null>(null);
  const [now, setNow] = useState(() => Date.now());
  const [turnstileToken, setTurnstileToken] = useState<string | null>(null);
  const [quoted, setQuoted] = useState<QuotedAddress | null>(null);
  const [quoting, setQuoting] = useState(false);
  /** Entrega impossível agora (fora da área, provedor fora do ar): mostra o atalho "Retirar no local". */
  const [offerPickup, setOfferPickup] = useState(false);
  /** O cliente confirmou a taxa nova: reenvia assim que houver um token novo do Turnstile. */
  const [resubmit, setResubmit] = useState(false);
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
        address: saved.address ? { ...EMPTY_ADDRESS, ...saved.address } : EMPTY_ADDRESS,
        paymentMethod: saved.paymentMethod,
      }));
    }
  }, [slug]);

  // Relógio: contagem regressiva dos 429 (1 s) e validade da cotação (15 min, conferida a cada 15 s).
  const ticking = Boolean(retryUntil || quoteRetryUntil);
  useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), ticking ? 1000 : 15_000);
    return () => clearInterval(t);
  }, [ticking]);
  useEffect(() => {
    if (retryUntil && now >= retryUntil) setRetryUntil(null);
    if (quoteRetryUntil && now >= quoteRetryUntil) setQuoteRetryUntil(null);
  }, [now, retryUntil, quoteRetryUntil]);

  useEffect(() => {
    if (message) messageRef.current?.focus();
  }, [message]);

  const menu = store.menu;
  const isDelivery = form.fulfillmentType === 'DELIVERY';
  const qState: QuoteState = quoteState(quoted, form.address, now);
  const deliveryFeeCents = isDelivery && quoted && qState === 'valid' ? parseMoney(quoted.quote.deliveryFee) : 0;
  const preview = menu ? previewCart(cart, menu, deliveryFeeCents) : null;
  const cartIssues: CartIssue[] = menu ? validateCart(cart, menu, store.limits) : [];
  const globalIssues = cartIssues.filter((i) => !('lineId' in i));
  const issuesByLine = new Map<string, CartIssue[]>();
  for (const issue of cartIssues) {
    if ('lineId' in issue) issuesByLine.set(issue.lineId, [...(issuesByLine.get(issue.lineId) ?? []), issue]);
  }
  const secondsLeft = retryUntil ? Math.max(0, Math.ceil((retryUntil - now) / 1000)) : 0;
  const quoteSecondsLeft = quoteRetryUntil ? Math.max(0, Math.ceil((quoteRetryUntil - now) / 1000)) : 0;
  const isOpen = store.status === 'OPEN' && Boolean(menu);
  const waitingCaptcha = captchaEnabled && !turnstileToken;
  const deliveryRuleText = describeDeliveryRule(store.deliveryRule);

  // A entrega saiu do ar enquanto o cliente montava o pedido: volta para retirada.
  useEffect(() => {
    if (!store.deliveryAvailable && form.fulfillmentType === 'DELIVERY' && store.pickupAvailable) {
      setForm((f) => ({ ...f, fulfillmentType: 'PICKUP' }));
    }
  }, [store.deliveryAvailable, store.pickupAvailable, form.fulfillmentType]);

  /** O token do Turnstile é de uso único (ADR-0045 item 12): entrega o atual e já pede outro. */
  function takeToken(): string | null {
    if (!captchaEnabled) return null;
    const token = turnstileToken;
    turnstile.current?.reset();
    return token;
  }

  function setField<K extends keyof CheckoutForm>(key: K, value: CheckoutForm[K]) {
    setForm((f) => ({ ...f, [key]: value }));
    setErrors((e) => ({ ...e, [key === 'name' ? 'customerName' : key === 'phone' ? 'customerPhone' : key]: undefined }));
    if (key === 'fulfillmentType') setOfferPickup(false);
  }

  function setAddress<K extends keyof DeliveryAddressInput>(key: K, value: string) {
    // Mudar rua, número, bairro, cidade ou CEP invalida a cotação (quoteState → 'stale'); complemento e referência não.
    setForm((f) => ({ ...f, address: { ...f.address, [key]: value } }));
    setErrors((e) => ({ ...e, [`deliveryAddress.${key}`]: undefined, deliveryQuoteId: undefined }));
    setOfferPickup(false);
  }

  async function handleQuote() {
    const addressErrors = validateAddress(form.address);
    if (Object.keys(addressErrors).length > 0) {
      setErrors((e) => ({ ...e, ...addressErrors, deliveryQuoteId: 'Revise o endereço para calcular a entrega.' }));
      return;
    }
    if (waitingCaptcha) {
      setErrors((e) => ({ ...e, deliveryQuoteId: 'Aguarde a verificação anti-robô terminar.' }));
      return;
    }
    setQuoting(true);
    setOfferPickup(false);
    const address = normalizeAddressInput(form.address);
    const result = await browserApi().quoteDelivery(slug, address, { turnstileToken: takeToken() });
    setQuoting(false);
    if (result.ok) {
      setQuoted({ quote: result.data, fingerprint: addressFingerprint(address) });
      setForm((f) => ({ ...f, deliveryQuoteId: result.data.quoteId }));
      setErrors((e) => ({ ...e, deliveryQuoteId: undefined }));
      return;
    }
    setQuoted(null);
    setForm((f) => ({ ...f, deliveryQuoteId: null }));
    const outcome = interpretQuoteFailure(result);
    switch (outcome.kind) {
      case 'address':
        setErrors((e) => ({ ...e, ...outcome.errors, deliveryQuoteId: outcome.message }));
        break;
      case 'outOfArea':
      case 'unavailable':
        setErrors((e) => ({ ...e, deliveryQuoteId: outcome.message }));
        setOfferPickup(store.pickupAvailable);
        break;
      case 'rateLimited':
        setQuoteRetryUntil(Date.now() + outcome.retryAfterSeconds * 1000);
        setNow(Date.now());
        setErrors((e) => ({ ...e, deliveryQuoteId: outcome.message }));
        break;
      case 'storeUnavailable':
        setErrors((e) => ({ ...e, deliveryQuoteId: outcome.message }));
        void refresh();
        break;
      case 'captcha':
      case 'message':
        setErrors((e) => ({ ...e, deliveryQuoteId: outcome.message }));
        break;
    }
  }

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    void submit();
  }

  async function submit() {
    if (submitting || !menu || !preview) return;
    setMessage(null);
    setLineErrors({});
    const localErrors = validateCheckout(form, preview, store.limits.notesMaxLength, isDelivery ? qState : 'none');
    setErrors(localErrors);
    if (Object.keys(localErrors).length > 0 || cartIssues.length > 0) {
      const onlyQuote = Boolean(localErrors.deliveryQuoteId) && Object.keys(localErrors).length === 1;
      setMessage({ tone: 'danger', text: onlyQuote ? localErrors.deliveryQuoteId! : 'Confira os campos destacados.' });
      return;
    }
    if (waitingCaptcha) {
      setMessage({ tone: 'warning', text: 'Aguarde a verificação anti-robô terminar.' });
      return;
    }

    const body = buildOrderRequest(form, cart, preview);
    const bodyJson = JSON.stringify(body);
    // Mesma chave em todo reenvio do mesmo corpo, inclusive depois de recarregar a página (§6.19). Corpo diferente
    // (taxa nova, carrinho recalculado, cliente editou) → chave nova.
    const attempt = keyForAttempt(bodyJson, loadPendingSubmission(slug));
    savePendingSubmission(slug, attempt);

    setSubmitting(true);
    const result = await browserApi().placeOrder(slug, body, {
      idempotencyKey: attempt.idempotencyKey,
      turnstileToken: takeToken(),
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
          address: isDelivery ? normalizeAddressInput(form.address) : (loadCustomer(slug)?.address ?? null),
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
    const outcome = interpretSubmitFailure(result);
    switch (outcome.kind) {
      case 'cartChanged':
        setCartChanged(outcome.cart);
        setMessage({ tone: 'warning', text: outcome.message });
        void refresh();
        break;
      case 'quoteChanged':
        setQuoteChanged(outcome.quote);
        setMessage({ tone: 'warning', text: outcome.message });
        break;
      case 'deliveryFailed':
        setQuoted(null);
        setForm((f) => ({ ...f, deliveryQuoteId: null }));
        setErrors(outcome.errors);
        setOfferPickup(store.pickupAvailable);
        setMessage({ tone: 'warning', text: outcome.message });
        break;
      case 'fieldErrors': {
        const items: Record<number, string> = {};
        const fields: FieldErrors = {};
        for (const [k, v] of Object.entries(outcome.errors)) {
          const m = /^items\[(\d+)\]/.exec(k);
          if (m) items[Number(m[1])] = [items[Number(m[1])], v].filter(Boolean).join(' ');
          else fields[k] = v;
        }
        if (fields.deliveryQuoteId || Object.keys(fields).some((k) => k.startsWith('deliveryAddress.'))) {
          // Cotação inexistente (9a) ou endereço não localizado no recálculo (9b): cotar de novo.
          setQuoted(null);
          setForm((f) => ({ ...f, deliveryQuoteId: null }));
          fields.deliveryQuoteId ??= 'Calcule a entrega de novo.';
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

  // Reenvio depois de confirmar a taxa nova: espera o token novo do Turnstile.
  const submitRef = useRef(submit);
  submitRef.current = submit;
  useEffect(() => {
    if (resubmit && !submitting && (!captchaEnabled || turnstileToken)) {
      setResubmit(false);
      void submitRef.current();
    }
  }, [resubmit, submitting, captchaEnabled, turnstileToken]);

  function confirmQuoteChanged() {
    if (!quoteChanged) return;
    // O endereço não mudou (a API recalculou para o mesmo): a cotação nova vale para ele.
    setQuoted({ quote: quoteChanged, fingerprint: addressFingerprint(form.address) });
    setForm((f) => ({ ...f, deliveryQuoteId: quoteChanged.quoteId }));
    setQuoteChanged(null);
    setMessage(null);
    setResubmit(true);
  }

  function switchToPickup() {
    setField('fulfillmentType', 'PICKUP');
    setQuoteChanged(null);
    setErrors((e) => ({ ...e, deliveryQuoteId: undefined }));
    setMessage(null);
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
                  {store.deliveryAvailable ? (deliveryRuleText ?? 'Taxa calculada pelo endereço') : 'Indisponível agora'}
                </span>
              </span>
            </label>
            <FieldError id={errId('fulfillmentType')} message={errors.fulfillmentType} />
          </fieldset>

          {isDelivery && store.deliveryAvailable && (
            <fieldset>
              <legend>Endereço de entrega</legend>
              {(
                [
                  ['street', 'Rua', 'address-line1', undefined],
                  ['number', 'Número', 'off', 'Ex.: 45 ou S/N'],
                  ['complement', 'Complemento (opcional)', 'address-line2', 'Ex.: casa 2, apto 101'],
                  ['neighborhood', 'Bairro', 'address-level3', undefined],
                  ['city', 'Cidade (opcional)', 'address-level2', store.address?.city ?? undefined],
                  ['zipCode', 'CEP (opcional)', 'postal-code', '00000-000'],
                  ['reference', 'Ponto de referência (opcional)', 'off', 'Ex.: em frente à padaria'],
                ] as const
              ).map(([k, label, ac, placeholder]) => (
                <label className="field" key={k}>
                  <span className="field-label">{label}</span>
                  <input
                    className="input"
                    autoComplete={ac}
                    inputMode={k === 'zipCode' ? 'numeric' : undefined}
                    placeholder={placeholder}
                    maxLength={k === 'street' || k === 'reference' ? 120 : k === 'number' ? 10 : k === 'city' ? 80 : k === 'zipCode' ? 9 : 60}
                    value={form.address[k] ?? ''}
                    onChange={(e) => setAddress(k, e.target.value)}
                    {...invalid(`deliveryAddress.${k}`)}
                  />
                  <FieldError id={errId(`deliveryAddress.${k}`)} message={errors[`deliveryAddress.${k}`]} />
                </label>
              ))}

              <div aria-live="polite">
                {quoted && qState === 'valid' ? (
                  <div className="notice notice-success">
                    <p>
                      Encontramos: <strong>{quoted.quote.formattedAddress}</strong>
                    </p>
                    <p>
                      <strong>{describeQuote(quoted.quote)}</strong>
                    </p>
                    <p className="small">Confira o endereço. Se não for esse, corrija e calcule de novo.</p>
                  </div>
                ) : (
                  <>
                    {quoted && qState === 'stale' && (
                      <p className="notice notice-warning">Você mudou o endereço. Calcule a entrega de novo.</p>
                    )}
                    {quoted && qState === 'expired' && (
                      <p className="notice notice-warning">A cotação da entrega venceu. Calcule de novo.</p>
                    )}
                    <button
                      type="button"
                      className="btn btn-block"
                      onClick={handleQuote}
                      disabled={quoting || quoteSecondsLeft > 0 || !isOpen}
                    >
                      {quoting ? 'Calculando…' : quoteSecondsLeft > 0 ? `Aguarde ${quoteSecondsLeft} s` : 'Calcular taxa de entrega'}
                    </button>
                  </>
                )}
              </div>
              <FieldError id={errId('deliveryQuoteId')} message={errors.deliveryQuoteId} />
              {offerPickup && store.pickupAvailable && (
                <button type="button" className="btn btn-primary btn-block" style={{ marginTop: '0.5rem' }} onClick={switchToPickup}>
                  Retirar no local
                </button>
              )}
            </fieldset>
          )}
        </section>

        {quoteChanged && (
          <section className="card" aria-labelledby="quote-changed-title">
            <h2 id="quote-changed-title">A taxa de entrega mudou</h2>
            <p>
              {describeQuote(quoteChanged)} para <strong>{quoteChanged.formattedAddress}</strong>.
            </p>
            {preview && (
              <p className="row-between">
                <strong>Novo total</strong>
                <span className="price">{formatBRL(preview.itemsCents + parseMoney(quoteChanged.deliveryFee))}</span>
              </p>
            )}
            <div className="stack">
              <button type="button" className="btn btn-primary btn-block" onClick={confirmQuoteChanged} disabled={submitting}>
                Confirmar e enviar
              </button>
              {store.pickupAvailable && (
                <button type="button" className="btn btn-block" onClick={switchToPickup}>
                  Prefiro retirar no local
                </button>
              )}
            </div>
          </section>
        )}

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
                <span className="price">
                  {quoted && qState === 'valid'
                    ? `${formatBRL(preview.deliveryFeeCents)}${quoted.quote.distanceKm ? ` (${quoted.quote.distanceKm.replace('.', ',')} km)` : ''}`
                    : 'a calcular'}
                </span>
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
          disabled={!isOpen || submitting || resubmit || secondsLeft > 0 || Boolean(cartChanged) || Boolean(quoteChanged)}
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
