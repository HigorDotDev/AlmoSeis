'use client';

import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { useCallback, useEffect, useRef, useState } from 'react';
import { browserApi } from '@/lib/api/client';
import type { WebOrderTracking } from '@/lib/api/types';
import { useVisibleInterval } from '@/lib/hooks';
import { formatApiMoney, formatBRL, parseMoney } from '@/lib/money';
import { trackingItemTotalCents } from '@/lib/tracking';
import { formatE164, whatsappLink } from '@/lib/phone';
import { findOrder, markOrderFinished, saveCart } from '@/lib/storage';
import {
  CANCELED_TEXT,
  EXPIRED_TEXT,
  FINAL_TRACKING_STATUSES,
  formatAddress,
  formatDateTime,
  formatTime,
  NOT_ACCEPTED_STATUSES,
  PAYMENT_TEXT,
  rejectionText,
  TIMELINE_TEXT,
  TRACKING_STATUS_TEXT,
  WITHDRAWN_TEXT,
} from '@/lib/texts';

const POLL_MS = 20_000;

type LoadError = 'not-found' | 'expired' | 'network' | null;

interface Props {
  slug: string;
  token: string;
  justSubmitted: boolean;
}

export function TrackingClient({ slug, token, justSubmitted }: Props) {
  const router = useRouter();
  const [data, setData] = useState<WebOrderTracking | null>(null);
  const [loadError, setLoadError] = useState<LoadError>(null);
  const [offline, setOffline] = useState(false);
  const [withdrawing, setWithdrawing] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const etag = useRef<string | null>(null);
  const dialog = useRef<HTMLDialogElement>(null);

  const isFinal = data ? FINAL_TRACKING_STATUSES.has(data.status) : false;

  const apply = useCallback(
    (tracking: WebOrderTracking) => {
      setData(tracking);
      if (tracking.store.slug !== slug) {
        // O restaurante trocou o slug: a página vai para o endereço atual (ADR-0047 item 3).
        router.replace(`/${tracking.store.slug}/pedido/${token}`);
      }
      if (FINAL_TRACKING_STATUSES.has(tracking.status)) markOrderFinished(tracking.store.slug, token);
    },
    [router, slug, token],
  );

  const poll = useCallback(async () => {
    const result = await browserApi().getTracking(token, etag.current);
    if (result.ok) {
      setOffline(false);
      etag.current = result.etag;
      if (result.status === 200) apply(result.data);
      return;
    }
    if (result.kind === 'problem' && result.status === 404) setLoadError('not-found');
    else if (result.kind === 'problem' && result.status === 410) setLoadError('expired');
    else setOffline(true);
  }, [token, apply]);

  useEffect(() => {
    void poll();
  }, [poll]);
  // A cada 20 s com a aba visível; para nos estados finais (ADR-0047 item 8).
  useVisibleInterval(() => void poll(), POLL_MS, !isFinal && !loadError);

  async function withdraw() {
    dialog.current?.close();
    setWithdrawing(true);
    const result = await browserApi().withdraw(token);
    setWithdrawing(false);
    if (result.ok) {
      etag.current = result.headers.get('ETag');
      apply(result.data);
      return;
    }
    const code = result.kind === 'problem' ? result.problem.errorCode : null;
    if (code === 'OrderNotAwaitingAcceptance') setNotice('O restaurante já respondeu ao pedido. Para desistir agora, fale com o restaurante.');
    else if (code === 'WebOrderExpired') setNotice('O prazo para o restaurante confirmar já acabou.');
    else if (result.kind === 'problem' && result.status === 429) setNotice('Muitas tentativas. Aguarde um pouco e tente de novo.');
    else setNotice('Não foi possível desistir agora. Tente de novo.');
    etag.current = null;
    void poll();
  }

  function tryAgain() {
    const saved = findOrder(data?.store.slug ?? slug, token);
    if (saved?.cart.lines.length) saveCart(data?.store.slug ?? slug, saved.cart);
    router.push(`/${data?.store.slug ?? slug}/carrinho`);
  }

  async function copyLink() {
    try {
      await navigator.clipboard.writeText(window.location.origin + window.location.pathname);
      setCopied(true);
    } catch {
      setCopied(false);
    }
  }

  if (loadError === 'not-found' || loadError === 'expired') {
    return (
      <main id="conteudo" className="page">
        <h1>{loadError === 'expired' ? 'Este acompanhamento expirou' : 'Pedido não encontrado'}</h1>
        <p>
          {loadError === 'expired'
            ? 'O link de acompanhamento vale por 7 dias.'
            : 'Confira se o link está completo. Se precisar, fale com o restaurante.'}
        </p>
        <Link href={`/${slug}`} className="btn btn-primary">
          Ir para a página do restaurante
        </Link>
      </main>
    );
  }

  if (!data) {
    return (
      <main id="conteudo" className="page">
        <h1>Acompanhar pedido</h1>
        <p className="muted" role="status">
          {offline ? 'Sem conexão. Tentando de novo…' : 'Carregando…'}
        </p>
      </main>
    );
  }

  const phone = formatE164(data.store.contactPhone);
  const notAccepted = NOT_ACCEPTED_STATUSES.has(data.status);
  const rejection = data.status === 'REJECTED' && data.rejection ? rejectionText(data.rejection.reasonCode, data.rejection.message) : null;
  const pageUrl = typeof window !== 'undefined' ? window.location.origin + window.location.pathname : '';

  return (
    <main id="conteudo" className="page">
      <p className="muted small">{data.store.name}</p>

      {justSubmitted && !notAccepted && (
        <div className="notice notice-success">
          <h2>Pedido enviado!</h2>
          <p>Guarde este link para acompanhar o pedido.</p>
          <div className="stack">
            <button type="button" className="btn btn-block" onClick={copyLink}>
              {copied ? 'Link copiado' : 'Copiar link'}
            </button>
            <a
              className="btn btn-block"
              href={whatsappLink(null, `Meu pedido na ${data.store.name}: ${pageUrl}`)}
              target="_blank"
              rel="noopener noreferrer"
            >
              Enviar para mim no WhatsApp
            </a>
          </div>
        </div>
      )}

      <section className="card" aria-live="polite">
        <h1 className="status-title">{TRACKING_STATUS_TEXT[data.status]}</h1>
        <p className="muted">
          Pedido de {data.customerFirstName}
          {data.orderNumber != null && <> · nº {data.orderNumber}</>} · enviado às {formatTime(data.submittedAt)}
        </p>

        {data.status === 'AWAITING_ACCEPTANCE' && data.acceptanceDeadline && (
          <p>O restaurante tem até as {formatTime(data.acceptanceDeadline)} para confirmar.</p>
        )}
        {data.estimatedReadyMinutes != null && !isFinal && data.acceptanceStatus === 'ACCEPTED' && (
          <p>Tempo estimado: {data.estimatedReadyMinutes} min.</p>
        )}
        {rejection && (
          <>
            <p>
              <strong>{rejection.title}</strong>
            </p>
            {rejection.detail && <p>{rejection.detail}</p>}
          </>
        )}
        {data.status === 'EXPIRED' && <p>{EXPIRED_TEXT}</p>}
        {data.status === 'WITHDRAWN' && <p>{WITHDRAWN_TEXT}</p>}
        {data.status === 'CANCELED' && <p>{CANCELED_TEXT}</p>}
        {data.changedByStore && !notAccepted && (
          <div className="notice notice-warning">O restaurante alterou seu pedido. Confira os itens e o total abaixo.</div>
        )}
        {offline && <p className="muted small">Sem conexão. Tentando de novo…</p>}
        {notice && (
          <div className="notice notice-warning" role="alert">
            {notice}
          </div>
        )}

        {data.canWithdraw && (
          <button type="button" className="btn btn-block" onClick={() => dialog.current?.showModal()} disabled={withdrawing}>
            {withdrawing ? 'Desistindo…' : 'Desistir do pedido'}
          </button>
        )}

        {notAccepted && (
          <div className="stack" style={{ marginTop: '0.75rem' }}>
            {data.status === 'EXPIRED' ? (
              <button type="button" className="btn btn-primary btn-block" onClick={tryAgain}>
                Tentar de novo
              </button>
            ) : (
              <Link href={`/${data.store.slug}`} className="btn btn-primary btn-block">
                Fazer outro pedido
              </Link>
            )}
          </div>
        )}
        {(notAccepted || data.status === 'AWAITING_ACCEPTANCE') && phone && data.store.contactPhone && (
          <p className="small" style={{ marginTop: '0.75rem' }}>
            Falar com o restaurante:{' '}
            <a href={whatsappLink(data.store.contactPhone)} target="_blank" rel="noopener noreferrer">
              {phone}
            </a>
          </p>
        )}
      </section>

      <section className="card" aria-labelledby="linha-title">
        <h2 id="linha-title">Andamento</h2>
        <ol className="timeline">
          {data.timeline.map((t, i) => (
            <li key={`${t.step}-${i}`}>
              <strong>{TIMELINE_TEXT[t.step] ?? t.step}</strong> <span className="muted">· {formatTime(t.at)}</span>
            </li>
          ))}
        </ol>
      </section>

      <section className="card" aria-labelledby="itens-title">
        <h2 id="itens-title">Itens</h2>
        <ul className="list">
          {data.items.map((item, i) => (
            <li key={i}>
              <div className="row-between">
                <strong>
                  {item.quantity}× {item.name}
                </strong>
                {/* Combo: o total é a soma da árvore (combo + partes + adicionais das partes). */}
                <span className="price">
                  {item.parts?.length ? formatBRL(trackingItemTotalCents(item)) : formatApiMoney(item.lineTotal)}
                </span>
              </div>
              {item.parts && item.parts.length > 0 && (
                <ul className="combo-parts" aria-label={`Itens de ${item.name}`}>
                  {item.parts.map((part, k) => (
                    <li key={k}>
                      <div className="row-between small">
                        <span>
                          {part.quantity}× {part.name}
                        </span>
                        {parseMoney(part.lineTotal) > 0 && <span className="price">+ {formatApiMoney(part.lineTotal)}</span>}
                      </div>
                      {part.addons.map((a, j) => (
                        <div key={j} className="row-between small">
                          <span>
                            + {a.quantity}× {a.name}
                          </span>
                          <span className="price">{formatApiMoney(a.lineTotal)}</span>
                        </div>
                      ))}
                      {part.notes && <p className="muted small">Obs.: {part.notes}</p>}
                    </li>
                  ))}
                </ul>
              )}
              {item.composition && <p className="muted small">{item.composition}</p>}
              {item.addons.map((a, j) => (
                <div key={j} className="row-between small">
                  <span>
                    + {a.quantity}× {a.name}
                  </span>
                  <span className="price">{formatApiMoney(a.lineTotal)}</span>
                </div>
              ))}
              {item.notes && <p className="muted small">Obs.: {item.notes}</p>}
            </li>
          ))}
        </ul>
        <p className="row-between">
          <span>Itens</span>
          <span className="price">{formatApiMoney(data.itemsTotal)}</span>
        </p>
        {data.fulfillmentType === 'DELIVERY' && (
          <p className="row-between">
            <span>Taxa de entrega{data.deliveryDistanceKm ? ` (${data.deliveryDistanceKm.replace('.', ',')} km)` : ''}</span>
            <span className="price">{formatApiMoney(data.deliveryFee)}</span>
          </p>
        )}
        <p className="row-between">
          <strong>Total</strong>
          <strong className="price">{formatApiMoney(data.totalAmount)}</strong>
        </p>
      </section>

      <section className="card" aria-labelledby="receber-title">
        <h2 id="receber-title">{data.fulfillmentType === 'DELIVERY' ? 'Entrega' : 'Retirada'}</h2>
        <p>
          {data.fulfillmentType === 'DELIVERY'
            ? [data.deliveryAddress?.street, data.deliveryAddress?.number].filter(Boolean).join(', ')
            : formatAddress(data.store.address) ?? data.store.name}
        </p>
        {data.expectedPaymentMethod && (
          <p>
            Pagamento: {PAYMENT_TEXT[data.expectedPaymentMethod]}
            {data.expectedPaymentMethod === 'CASH' && data.changeFor && <> · troco para {formatApiMoney(data.changeFor)}</>}
          </p>
        )}
        <p className="muted small">Este link vale até {formatDateTime(data.trackingExpiresAt)}.</p>
      </section>

      <dialog ref={dialog} aria-labelledby="desistir-title">
        <h2 id="desistir-title">Desistir do pedido?</h2>
        <p>O restaurante será avisado e o pedido não será preparado.</p>
        <div className="stack">
          <button type="button" className="btn btn-primary btn-block" onClick={withdraw}>
            Sim, desistir
          </button>
          <button type="button" className="btn btn-block" onClick={() => dialog.current?.close()}>
            Voltar
          </button>
        </div>
      </dialog>
    </main>
  );
}
