# ADR-0012 — Controle de concorrência otimista

- **Status:** Aceito
- **Data:** 2026-10-27
- **Origem:** EP-001 §9.2, §16
- **Refinado por:** ADR-0034

## Contexto

Vários funcionários alteram a mesma comanda ao mesmo tempo (atendente adiciona bebida enquanto o caixa registra o pagamento). Travas pessimistas seguradas pelo cliente não funcionam bem no mobile.

## Decisão

As entidades sujeitas a concorrência têm **versão**. Toda atualização informa a versão conhecida, e a API rejeita a gravação se a versão estiver desatualizada, devolvendo o estado atual para a interface se atualizar. O mecanismo concreto está no ADR-0034.

## Consequências

- O app nunca sobrescreve silenciosamente dados mais recentes (RN-014.10). Em caso de conflito, mostra a diferença e pede que o usuário refaça a ação.
