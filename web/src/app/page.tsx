import type { Metadata } from 'next';

export const metadata: Metadata = { title: 'AlmoSeis', robots: { index: false, follow: false } };

/** A raiz fica reservada ao marketplace da Fase 3 (ADR-0044 item 17). Por enquanto, só um aviso. */
export default function Home() {
  return (
    <main id="conteudo" className="page">
      <h1>AlmoSeis</h1>
      <p>Para fazer um pedido, abra o link que o restaurante enviou.</p>
    </main>
  );
}
