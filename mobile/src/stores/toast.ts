import { create } from 'zustand';

export type ToastTone = 'success' | 'error' | 'info';

interface Toast {
  id: number;
  message: string;
  tone: ToastTone;
}

interface ToastState {
  toasts: Toast[];
  show(message: string, tone?: ToastTone): void;
  dismiss(id: number): void;
}

let seq = 0;

export const useToast = create<ToastState>((set, get) => ({
  toasts: [],
  show(message, tone = 'success') {
    const id = ++seq;
    set({ toasts: [...get().toasts.slice(-2), { id, message, tone }] });
    setTimeout(() => get().dismiss(id), 3500);
  },
  dismiss: (id) => set((s) => ({ toasts: s.toasts.filter((t) => t.id !== id) })),
}));

export const toast = (message: string, tone?: ToastTone) => useToast.getState().show(message, tone);
