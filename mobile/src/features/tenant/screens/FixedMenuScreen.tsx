/** Menu lateral → "Cardápio" da lanchonete (ADR-0055 item 6, ADR-0056). O cadastro chega na fatia 2 do EP-003. */
import { Stack } from 'expo-router';

import { ComingSoon } from '../ComingSoon';

export default function FixedMenuScreen() {
  return (
    <>
      <Stack.Screen options={{ title: 'Cardápio' }} />
      <ComingSoon
        icon="book-outline"
        title="Cardápio fixo"
        text="Aqui você vai cadastrar as categorias, os pratos, as bebidas, os combos e os adicionais da lanchonete."
      />
    </>
  );
}
