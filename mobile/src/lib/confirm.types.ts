export interface ConfirmOptions {
  title: string;
  message?: string;
  /** Texto do botão que confirma (ex.: "Encerrar", "Descartar"). */
  confirmText: string;
  cancelText?: string;
  onConfirm: () => void;
}
