import { View } from 'react-native';

import { Button, EmptyState, useColors } from '@/components/ui';

/**
 * Web (ADR-0053 §6.3): a página já foi recarregada uma vez e a API ainda recusa a versão. O deploy do app web
 * está atrasado em relação à API. Sem link de loja (ADR-0017 continua: nada de compra).
 */
export default function UpdateRequiredScreen() {
  const c = useColors();
  return (
    <View style={{ flex: 1, justifyContent: 'center', padding: 24, backgroundColor: c.bg }}>
      <EmptyState
        icon="cloud-download-outline"
        title="Estamos atualizando o AlmoSeis"
        text="Tente de novo em alguns minutos."
        action={<Button title="Recarregar" icon="refresh" onPress={() => window.location.reload()} />}
      />
    </View>
  );
}
