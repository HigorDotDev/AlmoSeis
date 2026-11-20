'use client';

interface Props {
  value: number;
  min?: number;
  max: number;
  onChange: (value: number) => void;
  /** Nome do item, para o leitor de tela ("Diminuir Coca-Cola"). */
  label: string;
}

export function Stepper({ value, min = 0, max, onChange, label }: Props) {
  return (
    <div className="stepper" role="group" aria-label={`Quantidade de ${label}`}>
      <button
        type="button"
        className="btn"
        onClick={() => onChange(value - 1)}
        disabled={value <= min}
        aria-label={`Diminuir ${label}`}
      >
        −
      </button>
      <output aria-live="polite">{value}</output>
      <button
        type="button"
        className="btn"
        onClick={() => onChange(value + 1)}
        disabled={value >= max}
        aria-label={`Aumentar ${label}`}
      >
        +
      </button>
    </div>
  );
}
