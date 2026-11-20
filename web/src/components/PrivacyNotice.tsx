import type { PublicPrivacy, ThirdParty } from '@/lib/api/types';
import { formatE164 } from '@/lib/phone';

const THIRD_PARTY_TEXT: Record<ThirdParty, string> = {
  ANTI_BOT: 'Cloudflare Turnstile, para bloquear robôs (processado fora do Brasil)',
  MAPS: 'um provedor de mapas, para calcular a taxa de entrega (recebe só o endereço; processado fora do Brasil)',
  PAYMENTS: 'o provedor de pagamento, para cobrar o pedido online',
};

/**
 * Aviso de privacidade antes do botão "Enviar pedido" (ADR-0052 item 1, RN-203.8). Sem caixa de consentimento:
 * a base legal é a execução do pedido. *Texto sujeito a revisão jurídica.*
 */
export function PrivacyNotice({ privacy, storeName }: { privacy: PublicPrivacy | null; storeName: string }) {
  const controller = privacy?.controllerName ?? storeName;
  const phone = formatE164(privacy?.controllerPhone);
  const operator = privacy?.operatorName ?? 'AlmoSeis';
  const thirdParties = privacy?.thirdParties ?? [];
  return (
    <div className="notice notice-info small">
      <p>
        Seus dados são usados por <strong>{controller}</strong> para preparar e entregar o pedido.
      </p>
      <details>
        <summary>Privacidade</summary>
        <div className="stack" style={{ marginTop: '0.5rem' }}>
          <p>
            <strong>Quem cuida dos seus dados:</strong> {controller}
            {phone ? ` (contato: ${phone})` : ''}, que é o controlador. A {operator} opera a página em nome do restaurante.
          </p>
          <p>
            <strong>Para quê:</strong> preparar, entregar e falar com você sobre este pedido. Pedimos só nome, telefone, endereço
            (na entrega), observações e a forma de pagamento prevista.
          </p>
          {thirdParties.length > 0 && (
            <div>
              <strong>Quem mais recebe dados:</strong>
              <ul>
                {thirdParties.map((t) => (
                  <li key={t}>{THIRD_PARTY_TEXT[t] ?? t}</li>
                ))}
              </ul>
            </div>
          )}
          <p>
            <strong>Neste aparelho:</strong> se você marcar &quot;Lembrar meus dados&quot;, nome, telefone e endereço ficam só neste
            navegador para o próximo pedido. &quot;Esquecer meus dados&quot;, no fim da página, apaga tudo.
          </p>
          <p>
            <strong>Para pedir a exclusão dos seus dados</strong>, fale com {controller}.
          </p>
        </div>
      </details>
    </div>
  );
}
