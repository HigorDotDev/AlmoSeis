# ADR-0040 — E-mail transacional via Azure Communication Services

- **Status:** Aceito (recomendação aceita pelo usuário em 2026-10-27)
- **Data:** 2026-10-27
- **Origem:** Lacuna encontrada ao escrever `06-contratos-api.md` §6.3: o ADR-0018 exige e-mail do proprietário "para recuperação de senha", mas nenhum provedor de envio estava definido
- **Relacionados:** ADR-0018, ADR-0027, ADR-0037

## Contexto

Funcionários recuperam a senha por código emitido pelo proprietário (ADR-0018). O proprietário, porém, não tem a quem pedir. O único canal que ele cadastrou com esse fim é o e-mail. SMS e WhatsApp foram deliberadamente deixados de fora do MVP por custo.

## Decisão

1. **Azure Communication Services (ACS) Email**, com domínio próprio verificado (SPF/DKIM) e remetente `nao-responda@<domínio>`. Fica no mesmo provedor do ADR-0037, com segredo no Key Vault.
2. A integração fica atrás de `IEmailSender` no módulo Identity. Templates em pt-BR versionados no repositório.
3. **Uso no MVP:** somente a recuperação de senha (`POST /auth/password/forgot`): link de uso único, válido por 30 min, com token do ASP.NET Identity (sem tabela própria, invalidado pela troca do security stamp).
4. A resposta é sempre `202`, mesmo para e-mail inexistente, para não revelar contas.
5. Os links de pagamento da assinatura continuam sendo enviados pelo gateway (ADR-0017), não por este serviço.

## Alternativas consideradas

- **SendGrid/Mailgun/Amazon SES:** equivalentes, mas adicionam outro fornecedor e outro contrato.
- **Recuperação só via suporte (admin):** inviável para um produto de autocadastro.

## Consequências

- O domínio precisa estar configurado antes do piloto.
- Confirmação de e-mail no cadastro fica fora do MVP: `email_confirmed` existe, mas não bloqueia nada.
