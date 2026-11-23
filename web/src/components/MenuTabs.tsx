'use client';

import { useRef, type KeyboardEvent, type ReactNode } from 'react';
import type { PageTab } from '@/lib/fixed-menu';

interface Props {
  tabs: PageTab[];
  selectedKey: string;
  onSelect: (key: string) => void;
  renderPanel: (tab: PageTab) => ReactNode;
}

const tabId = (key: string) => `aba-${key}`;
const panelId = (key: string) => `painel-${key}`;

/**
 * Abas do cardápio (RN-316.1): padrão WAI-ARIA de abas com ativação manual (setas, Home e End movem o foco e
 * selecionam). Todos os painéis vão no HTML (renderização do servidor); os não selecionados ficam `hidden`.
 * Uma aba só → mostra o conteúdo sem a barra.
 */
export function MenuTabs({ tabs, selectedKey, onSelect, renderPanel }: Props) {
  const refs = useRef<Record<string, HTMLButtonElement | null>>({});
  if (tabs.length === 0) return null;
  const current = tabs.some((t) => t.key === selectedKey) ? selectedKey : tabs[0]!.key;

  if (tabs.length === 1) return <>{renderPanel(tabs[0]!)}</>;

  function onKeyDown(e: KeyboardEvent<HTMLButtonElement>, index: number) {
    let next: number | null = null;
    if (e.key === 'ArrowRight') next = (index + 1) % tabs.length;
    else if (e.key === 'ArrowLeft') next = (index - 1 + tabs.length) % tabs.length;
    else if (e.key === 'Home') next = 0;
    else if (e.key === 'End') next = tabs.length - 1;
    if (next === null) return;
    e.preventDefault();
    const key = tabs[next]!.key;
    onSelect(key);
    refs.current[key]?.focus();
  }

  return (
    <>
      <div className="tabs" role="tablist" aria-label="Cardápio">
        {tabs.map((tab, i) => {
          const selected = tab.key === current;
          return (
            <button
              key={tab.key}
              ref={(el) => {
                refs.current[tab.key] = el;
              }}
              type="button"
              role="tab"
              id={tabId(tab.key)}
              className="tab"
              aria-selected={selected}
              aria-controls={panelId(tab.key)}
              tabIndex={selected ? 0 : -1}
              onClick={() => onSelect(tab.key)}
              onKeyDown={(e) => onKeyDown(e, i)}
            >
              {tab.label}
            </button>
          );
        })}
      </div>
      {tabs.map((tab) => (
        <div key={tab.key} role="tabpanel" id={panelId(tab.key)} aria-labelledby={tabId(tab.key)} hidden={tab.key !== current} tabIndex={0}>
          {renderPanel(tab)}
        </div>
      ))}
    </>
  );
}
