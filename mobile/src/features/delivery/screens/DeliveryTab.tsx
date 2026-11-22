/** Aba Delivery (ADR-0055 item 3, ADR-0059). O conteúdo chega na fatia 4 do EP-003. */
import { ComingSoon } from '@/features/tenant/ComingSoon';

export default function DeliveryTab() {
  return (
    <ComingSoon
      icon="bicycle-outline"
      title="Delivery"
      text="Aqui vão ficar os pedidos de entrega e retirada do cardápio fixo, feitos por telefone e pelo site."
    />
  );
}
