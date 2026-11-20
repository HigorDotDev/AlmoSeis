import type { Metadata } from 'next';
import { CheckoutClient } from '@/components/CheckoutClient';
import { StoreLoadError } from '@/components/StoreLoadError';
import { loadStoreForPage } from '@/lib/load-store';

interface Props {
  params: Promise<{ slug: string }>;
}

export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const { slug } = await params;
  const store = await loadStoreForPage(slug, '/carrinho');
  return {
    title: store === 'error' ? 'Seu pedido' : `Seu pedido · ${store.name}`,
    robots: { index: false, follow: false },
  };
}

/** Carrinho e envio (F-204). O carrinho vive só no navegador (ADR-0052); o servidor entrega o perfil atualizado. */
export default async function CartPage({ params }: Props) {
  const { slug } = await params;
  const store = await loadStoreForPage(slug, '/carrinho');
  if (store === 'error') return <StoreLoadError />;
  return <CheckoutClient initialStore={store} />;
}
