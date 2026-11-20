/** M2 (web): Enter segurado no diálogo de confirmação não confirma de novo (`KeyboardEvent.repeat`). */
import { renderHook } from '@testing-library/react-native';

type Listener = (e: KeyboardEvent) => void;
const listeners = new Set<Listener>();

class FakeElement {
  constructor(
    readonly tagName: string,
    private readonly role: string | null = null,
  ) {}
  getAttribute(name: string) {
    return name === 'role' ? this.role : null;
  }
}

Object.defineProperty(globalThis, 'HTMLElement', { value: FakeElement, configurable: true, writable: true });
Object.defineProperty(globalThis, 'document', {
  value: {
    addEventListener: (_t: string, l: Listener) => listeners.add(l),
    removeEventListener: (_t: string, l: Listener) => listeners.delete(l),
  },
  configurable: true,
  writable: true,
});

// eslint-disable-next-line import/first
import { useKeyDown } from '@/lib/keyboard.web';

function press(key: string, init: Partial<KeyboardEvent> = {}) {
  const e = { key, repeat: false, ctrlKey: false, altKey: false, metaKey: false, isComposing: false, target: null, preventDefault: jest.fn(), ...init };
  for (const l of listeners) l(e as unknown as KeyboardEvent);
  return e;
}

describe('useKeyDown (web)', () => {
  it('ignora a repetição da tecla segurada', async () => {
    const handler = jest.fn(() => true);
    await renderHook(() => useKeyDown(handler));
    press('Enter');
    press('Enter', { repeat: true });
    press('Enter', { repeat: true });
    expect(handler).toHaveBeenCalledTimes(1);
  });

  it('com o foco num botão, o Enter fica com o botão', async () => {
    const handler = jest.fn(() => true);
    await renderHook(() => useKeyDown(handler));
    press('Enter', { target: new FakeElement('DIV', 'button') as unknown as EventTarget });
    expect(handler).not.toHaveBeenCalled();
  });
});
