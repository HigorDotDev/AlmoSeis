import type { Metadata } from 'next';
import { TrackingClient } from '@/components/TrackingClient';

interface Props {
  params: Promise<{ slug: string; token: string }>;
  searchParams: Promise<{ enviado?: string }>;
}

/**
 * Acompanhamento (F-208, ADR-0047). Sem cache, sem indexação, sem Referer e sem recursos de terceiros (o token está
 * no caminho). Os headers vêm do `next.config.ts`; aqui a mesma coisa em `<meta>`. Os dados são buscados pelo
 * navegador direto na API (ADR-0051 item 2), para o limite `public-tracking` ver o IP do cliente.
 */
export const dynamic = 'force-dynamic';

export const metadata: Metadata = {
  title: 'Acompanhar pedido',
  robots: { index: false, follow: false, nocache: true },
  referrer: 'no-referrer',
};

export default async function TrackingPage({ params, searchParams }: Props) {
  const { slug, token } = await params;
  const { enviado } = await searchParams;
  return <TrackingClient slug={slug} token={token} justSubmitted={enviado === '1'} />;
}
