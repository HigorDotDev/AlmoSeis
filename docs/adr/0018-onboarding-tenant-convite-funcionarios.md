# ADR-0018 — Onboarding do estabelecimento e convite de funcionários

- **Status:** Aceito (recomendação aceita pelo usuário em 2026-10-27)
- **Data:** 2026-10-27
- **Origem:** Lacuna em RF-001 (RN-001.6, RN-001.8), F-001, F-002
- **Relacionados:** ADR-0017, ADR-0027

## Contexto

O épico tem `POST /employees/invitations`, mas não define quem cria o estabelecimento, como o convite chega ao funcionário (muitos não usam e-mail) nem o que acontece quando o convidado já tem conta.

## Decisão

1. **Autocadastro no app:** o proprietário cria a conta (nome, telefone, e-mail obrigatório para recuperação, senha) e o estabelecimento (nome, CPF/CNPJ, cidade, fuso horário com padrão `America/Sao_Paulo`). Isso cria `Tenant` + `Subscription(TRIALING)` + `TenantEmployee(Proprietário)`, com aceite dos Termos e do contrato de tratamento de dados (ADR-0024).
2. **Convite:** quem tem `employees.manage` gera um convite com perfis pré-definidos. O resultado é um **link com código de 6 caracteres**, válido por **72 h** e de uso único, compartilhado pelo próprio proprietário pela folha de compartilhamento do celular (WhatsApp). A plataforma não envia SMS no MVP.
3. **Aceite:** o convidado abre o link (deep link do app) ou digita o código. Se já tem conta, faz login e o vínculo é criado. Se não tem, cria as credenciais (telefone ou e-mail + senha).
4. Convites podem ser revogados enquanto pendentes. Toda emissão, aceite e revogação é auditada.

## Alternativas consideradas

- **Proprietário cadastra o funcionário com senha provisória:** a senha trafega por terceiros e a responsabilidade pela credencial fica confusa.
- **Convite por SMS ou e-mail enviado pela plataforma:** custo de SMS e baixa adesão ao e-mail no público.

## Consequências

- Deep links precisam estar configurados (Expo Router + App Links/Universal Links).
- A recuperação de senha de funcionário sem e-mail é feita pelo proprietário, que gera um código de redefinição pelo mesmo mecanismo de convite (ADR-0027).
