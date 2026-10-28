import { Linking, View } from 'react-native';

import { Button, EmptyState, useColors } from '@/components/ui';
import { useSession } from '@/stores/session';

/** 426 AppVersionUnsupported (ADR-0035): bloqueante. */
export default function UpdateRequired() {
  const info = useSession((s) => s.upgradeRequired);
  const c = useColors();
  return (
    <View style={{ flex: 1, justifyContent: 'center', padding: 24, backgroundColor: c.bg }}>
      <EmptyState
        icon="cloud-download-outline"
        title="Atualize o AlmoSeis para continuar"
        text={info?.minVersion ? `Esta versão não é mais aceita. Instale a versão ${info.minVersion} ou mais nova.` : 'Esta versão não é mais aceita.'}
        action={info?.storeUrl ? <Button title="Abrir a loja" icon="open-outline" onPress={() => void Linking.openURL(info.storeUrl!)} /> : undefined}
      />
    </View>
  );
}
