/** Pergunta de sim/não com ação destrutiva. Web: `confirm.web.ts` (o `Alert.alert` do react-native-web não faz nada). */
import { Alert } from 'react-native';

import type { ConfirmOptions } from './confirm.types';

export function confirmAction({ title, message, confirmText, cancelText = 'Voltar', onConfirm }: ConfirmOptions): void {
  Alert.alert(title, message, [
    { text: cancelText, style: 'cancel' },
    { text: confirmText, style: 'destructive', onPress: onConfirm },
  ]);
}
