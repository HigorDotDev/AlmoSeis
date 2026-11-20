import type { Metadata } from 'next';
import { StoreClient } from '@/components/StoreClient';
import { StoreLoadError } from '@/components/StoreLoadError';
import { loadStoreForPage } from '@/lib/load-store';
import { describeMenu, ogImage, ogTitle } from '@/lib/og';

interface Props {
  params: Promise<{ slug: string }>;
}

export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const { slug } = await params;
  const store = await loadStoreForPage(slug);
  if (store === 'error') return { title: 'AlmoSeis' };
  const title = ogTitle(store);
  const description = describeMenu(store);
  const image = ogImage(store);
  return {
    title,
    description,
    alternates: { canonical: `/${store.slug}` },
    robots: store.status === 'UNAVAILABLE' ? { index: false, follow: false } : undefined,
    openGraph: {
      type: 'website',
      locale: 'pt_BR',
      url: `/${store.slug}`,
      siteName: store.name,
      title,
      description,
      images: image ? [{ url: image, width: 1200, height: 630, alt: store.name }] : undefined,
    },
    twitter: { card: image ? 'summary_large_image' : 'summary', title, description },
  };
}

export default async function StorePage({ params }: Props) {
  const { slug } = await params;
  const store = await loadStoreForPage(slug);
  if (store === 'error') return <StoreLoadError />;
  return <StoreClient initialStore={store} />;
}
