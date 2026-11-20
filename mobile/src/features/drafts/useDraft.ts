/**
 * Rascunho de formulário (RN-014.3, 08 §8.4): salva a cada mudança com debounce de 500 ms; reabrir a tela
 * oferece "Continuar rascunho de 11:42?". Apagado só depois de o servidor confirmar (useCriticalMutation).
 */
import { useCallback, useEffect, useRef, useState } from 'react';

import { draftsRepo } from '@/db/draftsRepo';
import type { Draft, DraftKind } from '@/db/schema';
import { newKey } from '@/lib/ids';
import { useSession } from '@/stores/session';

export const DRAFT_DEBOUNCE_MS = 500;

/** Gravações ainda no debounce, por rascunho. */
const pendingWrites = new Map<string, () => Promise<void>>();

/** Grava já tudo o que está no debounce (ex.: antes de a web recarregar a página por um 426, ADR-0053 §6.3). */
export async function flushAllDrafts(): Promise<void> {
  const writes = [...pendingWrites.values()];
  pendingWrites.clear();
  await Promise.all(writes.map((w) => w()));
}

export function useDraft<T>(kind: DraftKind, targetId: string | null = null) {
  const tenantId = useSession((s) => s.me?.tenant.id ?? null);
  const [draftId, setDraftId] = useState<string>(() => newKey());
  const [offer, setOffer] = useState<Draft | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const dirty = useRef(false);

  useEffect(() => {
    if (!tenantId) return;
    let alive = true;
    void draftsRepo.latest(tenantId, kind, targetId).then((d) => {
      if (alive && d) setOffer(d);
    });
    return () => {
      alive = false;
    };
  }, [tenantId, kind, targetId]);

  const draftIdRef = useRef(draftId);
  useEffect(() => {
    draftIdRef.current = draftId;
  }, [draftId]);
  useEffect(
    () => () => {
      if (timer.current) clearTimeout(timer.current);
      pendingWrites.delete(draftIdRef.current);
    },
    [],
  );

  const save = useCallback(
    (value: T) => {
      if (!tenantId) return;
      dirty.current = true;
      if (timer.current) clearTimeout(timer.current);
      const write = () => draftsRepo.save({ id: draftId, tenantId, kind, targetId, formJson: JSON.stringify(value) });
      pendingWrites.set(draftId, write);
      timer.current = setTimeout(() => {
        pendingWrites.delete(draftId);
        void write();
      }, DRAFT_DEBOUNCE_MS);
    },
    [tenantId, draftId, kind, targetId],
  );

  /** Antes de enviar: grava já o estado final, para o debounce não recriar o rascunho depois da confirmação. */
  const flush = useCallback(
    async (value: T) => {
      if (timer.current) clearTimeout(timer.current);
      timer.current = null;
      pendingWrites.delete(draftId);
      if (!tenantId) return;
      await draftsRepo.save({ id: draftId, tenantId, kind, targetId, formJson: JSON.stringify(value) });
    },
    [tenantId, draftId, kind, targetId],
  );

  /** Aceita o rascunho oferecido: passa a gravar por cima dele e devolve o conteúdo. */
  const resume = useCallback((): T | null => {
    if (!offer) return null;
    setDraftId(offer.id);
    const value = JSON.parse(offer.formJson) as T;
    setOffer(null);
    return value;
  }, [offer]);

  const discardOffer = useCallback(async () => {
    if (offer) await draftsRepo.remove(offer.id);
    setOffer(null);
  }, [offer]);

  const discard = useCallback(async () => {
    if (timer.current) clearTimeout(timer.current);
    pendingWrites.delete(draftId);
    await draftsRepo.remove(draftId);
  }, [draftId]);

  return { draftId, save, flush, offer, resume, discardOffer, discard };
}
