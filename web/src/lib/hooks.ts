'use client';

import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from 'react';
import { useRouter } from 'next/navigation';
import { browserApi } from './api/client';
import type { PublicStore } from './api/types';
import { EMPTY_CART, type Cart } from './cart';
import { loadCart, loadOrders, saveCart, storageRevision, subscribeStorage, type SavedOrder } from './storage';

/** -1 no servidor e na hidratação; depois, a revisão do `localStorage`. */
function useStorageRevision(): number {
  return useSyncExternalStore(subscribeStorage, storageRevision, () => -1);
}

export function useCart(slug: string): { cart: Cart; hydrated: boolean; update: (fn: (cart: Cart) => Cart) => void } {
  const rev = useStorageRevision();
  const cart = useMemo(() => (rev < 0 ? EMPTY_CART : loadCart(slug)), [rev, slug]);
  const update = useCallback((fn: (cart: Cart) => Cart) => saveCart(slug, fn(loadCart(slug))), [slug]);
  return { cart, hydrated: rev >= 0, update };
}

export function useSavedOrders(slug: string): SavedOrder[] {
  const rev = useStorageRevision();
  return useMemo(() => (rev < 0 ? [] : loadOrders(slug)), [rev, slug]);
}

/** Executa `fn` agora e a cada `ms` enquanto a aba está visível (`visibilitychange`). */
export function useVisibleInterval(fn: () => void, ms: number, enabled = true): void {
  const fnRef = useRef(fn);
  fnRef.current = fn;
  useEffect(() => {
    if (!enabled) return;
    let timer: ReturnType<typeof setInterval> | null = null;
    const start = () => {
      if (timer) return;
      timer = setInterval(() => fnRef.current(), ms);
    };
    const stop = () => {
      if (timer) clearInterval(timer);
      timer = null;
    };
    const onVisibility = () => {
      if (document.visibilityState === 'visible') {
        fnRef.current();
        start();
      } else {
        stop();
      }
    };
    if (document.visibilityState === 'visible') start();
    document.addEventListener('visibilitychange', onVisibility);
    return () => {
      stop();
      document.removeEventListener('visibilitychange', onVisibility);
    };
  }, [ms, enabled]);
}

/**
 * Perfil e cardápio vivos no navegador (ADR-0051 item 1): a página chega renderizada do servidor e o navegador
 * busca de novo ao abrir e a cada 30 s visível, para pausa e "Acabou" aparecerem em até 30 s (RF-202).
 */
export function useLiveStore(initial: PublicStore): { store: PublicStore; refresh: () => Promise<PublicStore | null> } {
  const [store, setStore] = useState(initial);
  const router = useRouter();
  const slug = initial.slug;

  const refresh = useCallback(async () => {
    const result = await browserApi().getStore(slug);
    if (!result.ok) return null;
    if (result.data.slug !== slug) {
      // O navegador seguiu o 301 da API: o slug mudou enquanto a página estava aberta.
      router.replace(`/${result.data.slug}`);
      return null;
    }
    setStore(result.data);
    return result.data;
  }, [slug, router]);

  useEffect(() => {
    void refresh();
  }, [refresh]);
  useVisibleInterval(() => void refresh(), 30_000);

  return { store, refresh };
}
