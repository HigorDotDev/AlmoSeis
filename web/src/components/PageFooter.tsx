'use client';

import { useState } from 'react';
import { forgetEverything } from '@/lib/storage';

/** Rodapé com a marca e o "Esquecer meus dados" (ADR-0052 item 6). */
export function PageFooter() {
  const [done, setDone] = useState(false);
  return (
    <footer className="footer">
      <p>
        Página de pedidos feita com <strong>AlmoSeis</strong>.
      </p>
      <p>
        <button
          type="button"
          className="btn-link"
          onClick={() => {
            if (window.confirm('Apagar deste aparelho seu nome, telefone, endereço, o pedido em montagem e os links de acompanhamento?')) {
              forgetEverything();
              setDone(true);
            }
          }}
        >
          Esquecer meus dados
        </button>
      </p>
      <p aria-live="polite">{done ? 'Pronto: seus dados foram apagados deste aparelho.' : ''}</p>
    </footer>
  );
}
