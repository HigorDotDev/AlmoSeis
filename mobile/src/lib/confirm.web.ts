/** Web (ADR-0053): o `Alert.alert` do react-native-web é vazio; o diálogo do navegador responde a Enter e Esc. */
import type { ConfirmOptions } from './confirm.types';

export function confirmAction({ title, message, onConfirm }: ConfirmOptions): void {
  if (window.confirm(message ? `${title}\n\n${message}` : title)) onConfirm();
}
