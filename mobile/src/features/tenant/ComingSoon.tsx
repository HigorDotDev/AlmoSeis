/** Estado vazio honesto das telas das próximas fatias do EP-003: nenhum botão que não funciona. */
import { View } from 'react-native';

import { EmptyState, space, useColors, type IconName } from '@/components/ui';

export const COMING_SOON = 'Disponível em breve';

export function ComingSoon({ icon, title, text }: { icon: IconName; title: string; text: string }) {
  const c = useColors();
  return (
    <View style={{ flex: 1, padding: space.lg, backgroundColor: c.bg }}>
      <EmptyState icon={icon} title={`${title} · ${COMING_SOON}`} text={text} />
    </View>
  );
}

/** Salão da lanchonete (ADR-0058, fatia 3). */
export function ALaCarteComingSoon() {
  return (
    <ComingSoon
      icon="receipt-outline"
      title="Comandas"
      text="Aqui você vai abrir comandas por número ou mesa, com os itens do cardápio fixo, e mandar os pratos para a cozinha."
    />
  );
}
