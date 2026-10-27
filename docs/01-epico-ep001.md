# ÉPICO EP-001 — Plataforma Mobile de Gestão de Restaurantes

> **Nota de precedência (2026-10-27):** este é o texto do épico v1.1 como foi recebido. As lacunas e inconsistências encontradas nele foram resolvidas nos ADRs 0017–0037 (`docs/adr/README.md`). **Onde houver diferença, o ADR prevalece.** Exemplos:
> - a estrutura do §5.3 foi substituída pelo ADR-0029;
> - `DailyClosing` passa a pertencer a `BusinessDay` (ADR-0019);
> - o namespace `Restaurant.*` passa a ser `AlmoSeis.*`.

**Produto:** SaaS de gestão de restaurantes e marmitarias  
**Versão:** 1.1 — Requisitos revisados  
**Status:** Planejamento  
**Plataformas:** Android e iOS  
**Modelo de negócio:** SaaS B2B2E  
**Arquitetura:** Monólito modular, API REST e aplicativo mobile  
**Modelo comercial:** Assinatura mensal por estabelecimento, com teste gratuito de 7 dias

---

# 1. Visão geral

## 1.1. Descrição do produto

Desenvolver um aplicativo mobile para a gestão operacional e financeira de pequenos restaurantes, marmitarias e estabelecimentos que comercializam refeições prontas.

A aplicação permitirá registrar pedidos de marmitas, pratos feitos (PF) e refeições por quilo, gerenciar cardápios diários e semanais, acompanhar a preparação das refeições, registrar pagamentos e realizar fechamentos financeiros.

O aplicativo será projetado para funcionar exclusivamente em dispositivos móveis, eliminando a necessidade de computadores e sistemas de gestão tradicionais.

A plataforma adotará o modelo B2B2E (Business-to-Business-to-Employee), no qual a empresa desenvolvedora fornece o software a estabelecimentos comerciais, que disponibilizam seu uso aos funcionários conforme suas respectivas permissões.

A arquitetura será preparada para expansão com atendimento automatizado pelo WhatsApp, pagamentos online e outras integrações comerciais.

## 1.2. Objetivos de negócio

- Digitalizar comandas e pedidos.
- Reduzir erros e perda de informações.
- Agilizar o atendimento ao cliente.
- Centralizar pedidos e comunicação com a cozinha.
- Facilitar o gerenciamento de cardápios variáveis.
- Organizar vendas e recebimentos.
- Reduzir divergências no fechamento de caixa.
- Manter rastreabilidade das operações.
- Permitir a utilização simultânea por funcionários.
- Oferecer um produto acessível mediante assinatura mensal.

## 1.3. Público-alvo

Pequenos estabelecimentos que comercializam:

- Marmitas para entrega ou retirada.
- Pratos feitos (PF).
- Refeições por quilo.
- Bebidas e adicionais.

O público inicial será composto por restaurantes que ainda utilizam comandas físicas, WhatsApp e anotações manuais.

---

# 2. Estrutura de navegação

O aplicativo possuirá quatro abas horizontais superiores.

| Aba | Nome | Funcionalidade |
|---|---|---|
| 01 | Marmitas | Registro e edição de pedidos de marmitas |
| 02 | Salão | Registro de pedidos de PF e refeições por quilo |
| 03 | Pedidos | Gerenciamento da produção de marmitas e PFs |
| 04 | Fechamento | Resumo financeiro, conciliação e fechamento do dia |

As abas ficarão acessíveis conforme as permissões do funcionário.

## 2.1. Menu expansível

O cabeçalho conterá um menu expansível com as seguintes opções:

- Perfil do usuário.
- Dados do estabelecimento.
- Gerenciamento de funcionários.
- Perfis e permissões.
- Cadastro de pratos e bebidas.
- Cardápio diário.
- Cardápio semanal.
- Histórico e reutilização de cardápios.
- Configuração de preços.
- Formas de pagamento.
- Histórico de operações.
- Histórico de fechamentos.
- Configurações gerais.
- Assinatura e plano contratado.
- Encerrar sessão.

---

# 3. Requisitos funcionais

## RF-001 — Autenticação e modelo B2B2E

### Descrição

Permitir a utilização da plataforma por múltiplos estabelecimentos, com funcionários e permissões individuais.

### Regras de negócio

- RN-001.1: Cada estabelecimento possuirá um identificador único `TenantId`.
- RN-001.2: Usuários deverão utilizar credenciais individuais.
- RN-001.3: Os dados de cada estabelecimento deverão permanecer isolados.
- RN-001.4: Usuários não poderão acessar informações de estabelecimentos aos quais não estejam vinculados.
- RN-001.5: Cada ação deverá identificar o funcionário responsável.
- RN-001.6: O proprietário poderá convidar, cadastrar e desativar funcionários.
- RN-001.7: A desativação de um funcionário não excluirá seu histórico de operações.
- RN-001.8: Um usuário poderá possuir vínculos com múltiplos estabelecimentos, desde que autorizado.
- RN-001.9: A plataforma deverá controlar período gratuito, assinatura e situação comercial de cada estabelecimento.

### Perfis

| Perfil | Responsabilidade |
|---|---|
| Administrador da plataforma | Administração SaaS e assinaturas |
| Proprietário | Controle total do estabelecimento |
| Gerente | Administração operacional e financeira |
| Atendente | Registro e alteração de pedidos |
| Cozinha | Visualização e gestão da produção |
| Caixa | Pagamentos e fechamento |

As permissões deverão ser validadas no backend, independentemente da visibilidade de botões no aplicativo.

### Critérios de aceite

- Funcionários autenticam-se individualmente.
- Funcionários desativados não conseguem acessar recursos protegidos.
- Dois estabelecimentos não conseguem consultar ou alterar dados um do outro.
- Cada operação identifica o usuário e o estabelecimento.

---

## RF-002 — Cadastro de produtos e pratos

### Descrição

Permitir o cadastro de pratos, bebidas e adicionais utilizados nos pedidos.

### Informações

- Identificador.
- Nome.
- Descrição opcional.
- Categoria.
- Foto opcional.
- Preço padrão.
- Disponibilidade.
- Data de cadastro.
- Data da última atualização.

### Regras

- RN-002.1: Produtos poderão ser reutilizados em diferentes cardápios.
- RN-002.2: Alterações de preços não afetarão pedidos já registrados.
- RN-002.3: Produtos utilizados em pedidos não poderão ser excluídos fisicamente quando isso comprometer o histórico.
- RN-002.4: O sistema deverá disponibilizar um atalho para cadastro de pratos durante a edição do cardápio.
- RN-002.5: Bebidas poderão ser adicionadas aos pedidos das três modalidades.
- RN-002.6: Produtos poderão ser desativados sem perder seus registros históricos.

### Critérios de aceite

- Cadastrar produtos com ou sem foto.
- Utilizar imediatamente um novo produto no cardápio.
- Preservar o preço histórico dos produtos vendidos.

---

## RF-003 — Gestão de cardápios

### Descrição

Permitir cadastrar cardápios diários, semanais e reutilizar cardápios anteriores.

Os cardápios de marmitas e PF serão independentes.

### Modalidades

**Diário:** cardápio definido para determinada data.

**Semanal:** programação dos cardápios dos diferentes dias da semana.

**Reutilizável:** criação de cardápios a partir de dias ou semanas anteriores.

### Regras

- RN-003.1: O estabelecimento poderá criar cardápios específicos para marmitas e PF.
- RN-003.2: Na ausência de cardápio diário, a aba de registro deverá oferecer uma opção para cadastrá-lo.
- RN-003.3: O cardápio poderá ser criado antecipadamente.
- RN-003.4: Será possível reutilizar cardápios anteriores.
- RN-003.5: A reutilização criará uma nova configuração independente.
- RN-003.6: Produtos poderão ser marcados como disponíveis ou indisponíveis.
- RN-003.7: A criação de cardápios deverá permitir o cadastro rápido de novos pratos.
- RN-003.8: Cardápios semanais funcionarão como modelos para datas futuras.
- RN-003.9: Alterações nos cardápios não modificarão os itens de pedidos já registrados.

### Critérios de aceite

- Criar cardápio durante o registro de pedidos.
- Reutilizar o cardápio de ontem ou da semana anterior.
- Manter cardápios independentes para marmitas e PF.
- Registrar pedidos preservando valores e descrições praticados no momento da venda.

---

## RF-004 — Registro de pedidos de marmitas

### Descrição

Permitir registrar pedidos contendo uma ou várias marmitas, bebidas e adicionais.

### Informações

- Número da comanda.
- Nome do cliente.
- Telefone opcional.
- Data e horário.
- Origem do pedido.
- Tipo de atendimento: entrega ou retirada.
- Endereço de entrega, quando aplicável.
- Marmitas e respectivas configurações.
- Bebidas e adicionais.
- Observações.
- Total.
- Situação de pagamento.
- Situação da produção.

### Regras

- RN-004.1: Um pedido poderá conter múltiplas marmitas.
- RN-004.2: Cada marmita poderá ter tamanho, prato, acompanhamentos e observações.
- RN-004.3: Bebidas poderão ser adicionadas ao pedido.
- RN-004.4: O valor total será calculado automaticamente.
- RN-004.5: Pedidos não pagos poderão ser editados.
- RN-004.6: Pedidos integralmente pagos terão seus itens e valores bloqueados para edição comum.
- RN-004.7: Alterações deverão identificar o funcionário responsável.
- RN-004.8: O pedido receberá um número sequencial por estabelecimento.
- RN-004.9: Novas marmitas poderão ser acrescentadas enquanto a comanda permanecer aberta financeiramente.
- RN-004.10: Alterações relevantes durante a produção gerarão alertas à cozinha.

### Critérios de aceite

- Criar pedidos com múltiplas marmitas.
- Adicionar bebidas após o registro inicial.
- Atualizar o total automaticamente.
- Impedir alterações financeiras em pedidos pagos.

---

## RF-005 — Registro de PF no salão

### Descrição

Permitir registrar pedidos de pratos feitos utilizando um cardápio próprio.

### Regras

- RN-005.1: O pedido poderá ser associado a mesa, nome ou número de comanda.
- RN-005.2: Uma comanda poderá conter múltiplos PFs.
- RN-005.3: Bebidas e adicionais poderão ser acrescentados.
- RN-005.4: O sistema deverá utilizar o cardápio específico de PF.
- RN-005.5: Pedidos de PF deverão aparecer no painel de produção.
- RN-005.6: A comanda permanecerá editável enquanto não estiver integralmente paga.
- RN-005.7: Valores de PF serão identificados separadamente no fechamento.

### Critérios de aceite

- Registrar múltiplos PFs em uma mesma mesa.
- Adicionar bebidas posteriormente.
- Acompanhar o pedido na cozinha.
- Preservar a composição financeira da comanda.

---

## RF-006 — Registro de refeições por quilo

### Descrição

Permitir registrar comandas contendo múltiplas pesagens e bebidas.

### Informações

- Número da comanda.
- Mesa.
- Nome do cliente.
- Preço do quilo.
- Lista de pesagens.
- Valor individual das pesagens.
- Bebidas.
- Total.
- Situação de pagamento.

### Regras

- RN-006.1: Uma comanda poderá conter diversas pesagens.
- RN-006.2: Cada pesagem deverá possuir peso, preço por quilo e valor calculado.
- RN-006.3: O preço do quilo aplicado deverá ser preservado no histórico.
- RN-006.4: Será possível adicionar pesagens e bebidas enquanto a comanda não estiver paga.
- RN-006.5: O total deverá corresponder à soma das pesagens, bebidas e adicionais.
- RN-006.6: Todas as pesagens permanecerão disponíveis para conferência.
- RN-006.7: Refeições por quilo não aparecerão no painel de produção do MVP.
- RN-006.8: O sistema deverá utilizar precisão decimal definida para pesos e valores monetários.

### Critérios de aceite

- Registrar múltiplas pesagens.
- Adicionar bebidas.
- Recalcular o total.
- Consultar os valores individuais utilizados no fechamento.

---

## RF-007 — Gestão de pedidos e produção

### Descrição

Disponibilizar o acompanhamento dos pedidos de marmitas e PFs.

### Estados de produção

1. Aberto.
2. Em produção.
3. Concluído.

### Regras

- RN-007.1: Todo pedido de marmita ou PF deverá possuir um estado de produção.
- RN-007.2: Pedidos iniciarão como Abertos.
- RN-007.3: Alterações de status exigirão confirmação.
- RN-007.4: A exibição será agrupada na ordem: Abertos, Em produção, Concluídos.
- RN-007.5: Dentro de cada grupo, os pedidos serão ordenados por horário de criação, do mais antigo para o mais recente.
- RN-007.6: Pedidos deverão mostrar número, itens, observações, horário e situação.
- RN-007.7: Alterações relevantes gerarão alertas visuais.
- RN-007.8: Os estados financeiros e de produção serão independentes.
- RN-007.9: Pedidos concluídos e não pagos poderão receber novos itens.
- RN-007.10: Quando forem acrescentados itens que exijam preparo, pedidos concluídos deverão retornar a Em produção.
- RN-007.11: Um pedido pago poderá continuar transitando entre os estados de produção, sem permitir alteração dos itens vendidos.

### Critérios de aceite

- Visualizar pedidos por estado e data.
- Receber novos pedidos sem atualizar manualmente a tela.
- Atualizar a produção independentemente do pagamento.
- Impedir alterações não autorizadas nos itens de pedidos pagos.

---

## RF-008 — Alertas de alterações

### Descrição

Sinalizar alterações realizadas em pedidos existentes.

### Eventos

- Inclusão ou remoção de pratos.
- Alteração de quantidade.
- Inclusão ou remoção de bebidas.
- Alterações nas observações.
- Modificações relevantes à preparação.

### Regras

- RN-008.1: Alterações deverão gerar eventos identificáveis.
- RN-008.2: A comanda deverá exibir um indicador quando houver alterações pendentes de ciência.
- RN-008.3: O funcionário poderá abrir os detalhes das alterações.
- RN-008.4: Após consultar e confirmar ciência, o alerta será removido para aquele funcionário.
- RN-008.5: O histórico permanecerá armazenado.
- RN-008.6: O sistema deverá evitar notificações desnecessárias para o próprio responsável pela alteração.
- RN-008.7: Cada funcionário terá controle independente dos alertas reconhecidos.

### Critérios de aceite

- Exibir indicador de alteração.
- Mostrar quais itens foram modificados.
- Permitir confirmação de ciência.
- Manter histórico após o reconhecimento do alerta.

---

## RF-009 — Gestão de pagamentos

### Descrição

Registrar pagamentos dos pedidos, garantindo integridade financeira.

### Regras

- RN-009.1: Todo pedido possuirá a propriedade lógica `IsPaid`.
- RN-009.2: Novos pedidos iniciarão com `IsPaid = false`.
- RN-009.3: O backend calculará a situação financeira a partir dos pagamentos válidos.
- RN-009.4: O MVP permitirá registrar pagamentos em dinheiro, Pix e cartão.
- RN-009.5: O registro manual deverá identificar valor, modalidade, data e responsável.
- RN-009.6: O pagamento integral tornará `IsPaid = true`.
- RN-009.7: Pedidos pagos não poderão sofrer edição direta de itens e valores.
- RN-009.8: Correções financeiras deverão utilizar operações específicas e auditáveis.
- RN-009.9: Pagamentos não poderão ser confirmados duas vezes por reenvio da mesma operação.
- RN-009.10: A mudança do estado de produção continuará permitida após o pagamento, conforme permissões.

### Critérios de aceite

- Registrar pagamentos.
- Bloquear alterações nos itens após quitação integral.
- Evitar duplicação de pagamentos.
- Preservar registros financeiros anteriores.
- Permitir correções autorizadas com histórico.

---

## RF-010 — Fechamento financeiro e conciliação

### Descrição

A aba Fechamento deverá consolidar vendas, recebimentos e pendências das três modalidades.

O sistema também permitirá a conferência dos valores efetivamente recebidos.

### Indicadores

- Total vendido em marmitas.
- Total vendido em PF.
- Total vendido em refeições por quilo.
- Total geral de vendas.
- Total recebido.
- Total pendente de recebimento.
- Quantidade de pedidos.
- Valores por forma de pagamento.
- Cancelamentos.
- Estornos.
- Histórico diário e mensal.

### Regras de negócio

- RN-010.1: O fechamento parcial deverá atualizar seus indicadores conforme novos pedidos e pagamentos forem registrados.
- RN-010.2: Cada modalidade deverá apresentar seus valores individualmente.
- RN-010.3: O total geral deverá consolidar as três modalidades.
- RN-010.4: Pedidos não pagos comporão o total vendido, mas não o total recebido.
- RN-010.5: Cancelamentos e estornos deverão ser identificados separadamente.
- RN-010.6: O sistema deverá permitir consultar fechamentos anteriores.
- RN-010.7: A finalização do fechamento deverá exigir permissão específica e confirmação.
- RN-010.8: O fechamento deverá preservar um registro dos valores apurados naquele momento.

### Conciliação do caixa

- RN-010.9: O sistema deverá calcular o valor esperado em dinheiro com base nos recebimentos e saídas de caixa registrados.
- RN-010.10: O funcionário autorizado deverá informar o valor efetivamente contado no caixa físico.
- RN-010.11: O sistema deverá calcular a diferença entre o valor esperado e o valor informado.
- RN-010.12: Divergências deverão ser exibidas claramente.
- RN-010.13: Havendo divergência, será obrigatória a apresentação de uma justificativa para finalizar o fechamento.
- RN-010.14: O fechamento deverá registrar o funcionário responsável, a data, o horário e as eventuais justificativas.
- RN-010.15: Recebimentos por Pix e cartão deverão ser apresentados separadamente e não serão considerados dinheiro físico.
- RN-010.16: O MVP permitirá registrar saldo inicial, suprimentos e retiradas do caixa para calcular corretamente o dinheiro esperado.

### Fechamento por modalidade

| Modalidade | Vendido | Recebido | Pendente |
|---|---:|---:|---:|
| Marmitas | R$ 1.250 | R$ 1.100 | R$ 150 |
| PF | R$ 870 | R$ 800 | R$ 70 |
| Quilo | R$ 1.530 | R$ 1.530 | R$ 0 |
| **Total** | **R$ 3.650** | **R$ 3.430** | **R$ 220** |

*Valores ilustrativos.*

### Conferência do caixa físico

| Informação | Valor |
|---|---:|
| Saldo inicial | R$ 100 |
| Recebimentos em dinheiro | R$ 850 |
| Suprimentos | R$ 0 |
| Retiradas | R$ 200 |
| Dinheiro esperado | R$ 750 |
| Dinheiro informado | R$ 740 |
| **Diferença** | **-R$ 10** |

O fechamento desse exemplo exigirá justificativa para a diferença de R$ 10.

### Critérios de aceite

- Exibir corretamente os valores por modalidade.
- Diferenciar vendas de recebimentos.
- Calcular o saldo esperado em dinheiro.
- Comparar o saldo esperado com o valor contado.
- Exigir justificativa para divergências.
- Registrar o responsável pelo fechamento.
- Preservar o histórico financeiro.

---

## RF-011 — Pagamentos posteriores ao fechamento

### Descrição

Permitir registrar pagamentos de pedidos realizados em dias anteriores, mesmo quando o expediente original já tiver sido fechado.

### Regras de negócio

- RN-011.1: O fechamento de um expediente não impedirá o recebimento posterior de pedidos pendentes.
- RN-011.2: O pedido permanecerá associado à data original da venda.
- RN-011.3: O pagamento será associado à data efetiva do recebimento.
- RN-011.4: O fechamento original não poderá ser modificado automaticamente por pagamentos posteriores.
- RN-011.5: O recebimento posterior deverá aparecer no movimento financeiro do expediente em que foi recebido.
- RN-011.6: Os relatórios deverão distinguir data da venda e data do recebimento.
- RN-011.7: A consulta histórica poderá mostrar a situação financeira atualizada do pedido sem modificar o registro original do fechamento.
- RN-011.8: Estornos e ajustes financeiros posteriores deverão ser registrados como novas operações auditáveis.

### Exemplo

**Segunda-feira:** um pedido de R$ 35 é registrado, mas permanece sem pagamento. O expediente é fechado com R$ 35 pendentes.

**Terça-feira:** o cliente realiza o pagamento de R$ 35 via Pix.

Resultado:

- A venda permanece atribuída à segunda-feira.
- O recebimento é registrado na terça-feira.
- O fechamento original de segunda-feira permanece preservado.
- O histórico do pedido mostra que a dívida foi quitada na terça-feira.

### Critérios de aceite

- Receber pagamentos de pedidos antigos.
- Preservar o fechamento anterior.
- Registrar o pagamento no movimento do dia correto.
- Evitar contabilização duplicada da receita.
- Permitir rastrear a relação entre pagamento e pedido original.

---

## RF-012 — Confirmação de ações

### Descrição

Todas as ações que produzirem modificações persistentes deverão exigir confirmação explícita.

### Ações abrangidas

- Cadastro e edição de cardápios.
- Criação de pedidos.
- Inclusão ou remoção de itens.
- Alterações de status.
- Registro e correção de pagamentos.
- Cancelamentos.
- Fechamentos.
- Alterações de permissões.
- Modificações em configurações.

### Regras

- RN-012.1: A confirmação deverá informar a operação solicitada.
- RN-012.2: Quando aplicável, deverá apresentar valores ou dados afetados.
- RN-012.3: A inclusão de múltiplos itens poderá ser confirmada em uma única operação de salvamento.
- RN-012.4: Cancelar uma confirmação não deverá persistir alterações.
- RN-012.5: Confirmações críticas deverão ocorrer antes do envio da operação ao backend.

Consultas, navegação e digitação em formulários não exigirão confirmação.

---

## RF-013 — Histórico e auditoria

### Descrição

Registrar as ações executadas por funcionários, garantindo rastreabilidade.

### Dados mínimos

- Identificador.
- Estabelecimento.
- Usuário responsável.
- Data e horário.
- Entidade afetada.
- Tipo de operação.
- Valores anteriores relevantes.
- Valores posteriores relevantes.
- Origem da operação.

### Regras

- RN-013.1: Alterações de pedidos, cardápios, pagamentos, fechamentos e permissões deverão ser auditadas.
- RN-013.2: Funcionários não poderão editar registros de auditoria.
- RN-013.3: O histórico de uma comanda deverá ser consultável por usuários autorizados.
- RN-013.4: Eventos automáticos deverão identificar sua origem.
- RN-013.5: Alterações simultâneas deverão preservar seus respectivos registros.
- RN-013.6: Dados sensíveis desnecessários não deverão ser registrados.
- RN-013.7: Logs operacionais e eventos de negócio deverão possuir finalidades distintas.

### Critérios de aceite

- Identificar quem realizou cada alteração.
- Consultar os valores anteriores e novos.
- Rastrear alterações financeiras.
- Preservar os eventos após desativação de funcionários.

---

## RF-014 — Conectividade e recuperação de conexão

### Descrição

O aplicativo deverá operar prioritariamente conectado à API central, preservando informações locais importantes durante interrupções de conexão.

O MVP não exigirá operação offline integral.

### Regras de negócio

- RN-014.1: O aplicativo deverá informar visualmente quando estiver sem conexão.
- RN-014.2: O sistema poderá apresentar informações previamente consultadas e armazenadas em cache.
- RN-014.3: Rascunhos de pedidos ainda não enviados poderão ser preservados localmente.
- RN-014.4: Um rascunho local não poderá ser tratado como pedido confirmado pelo estabelecimento.
- RN-014.5: A interface deverá diferenciar informações sincronizadas de informações pendentes.
- RN-014.6: Após restabelecer a conexão, o aplicativo deverá recuperar as atualizações perdidas.
- RN-014.7: O reenvio de uma operação não poderá gerar pedidos ou pagamentos duplicados.
- RN-014.8: Operações pendentes de confirmação do servidor deverão possuir identificação única.
- RN-014.9: Alterações conflitantes realizadas em outros aparelhos deverão ser identificadas.
- RN-014.10: O aplicativo não poderá sobrescrever silenciosamente informações mais recentes armazenadas no servidor.
- RN-014.11: Operações financeiras não poderão ser consideradas concluídas antes da confirmação da API.
- RN-014.12: Se o aplicativo não conseguir determinar se uma gravação foi concluída, deverá consultar o estado da operação antes de oferecer novo envio.

### Comportamento esperado

**Quando a conexão cair:**

- Exibir indicador de ausência de internet.
- Preservar o rascunho em andamento.
- Informar quais operações exigem conexão.
- Não apresentar pedidos locais como confirmados.

**Quando a conexão retornar:**

- Revalidar a sessão do usuário.
- Consultar operações de resultado desconhecido.
- Atualizar as listas de pedidos.
- Recuperar alterações feitas por outros funcionários.
- Resolver ou sinalizar conflitos.
- Permitir o envio dos rascunhos válidos.

### Critérios de aceite

- Uma interrupção não causa perda do rascunho persistido localmente.
- O mesmo pedido não é registrado duas vezes após uma tentativa repetida de envio.
- A aplicação informa quando não consegue confirmar uma operação.
- As alterações feitas por outros funcionários aparecem após reconexão.
- Conflitos são identificados e não sobrescrevem dados silenciosamente.

---

# 4. Requisitos não funcionais

## RNF-001 — Usabilidade

- Interface totalmente mobile.
- Compatibilidade com aparelhos Android de entrada.
- Áreas de toque adequadas ao atendimento rápido.
- Formulários simplificados.
- Alertas e confirmações objetivos.
- Diferenciação visual entre status operacionais e financeiros.
- Persistência de rascunhos quando apropriado.

## RNF-002 — Desempenho

Metas iniciais para testes:

- Respostas de operações comuns em até 1 segundo no percentil 95, sob condições definidas.
- Propagação de alterações entre dispositivos conectados em até 3 segundos, como meta.
- Carregamento otimizado de cardápios e produtos.
- Paginação de históricos e listas extensas.
- Cache local das informações adequadas.

## RNF-003 — Segurança

- HTTPS obrigatório.
- Autenticação com tokens.
- Autorização por permissões.
- Isolamento entre estabelecimentos.
- Armazenamento seguro de tokens no dispositivo.
- Validação de todas as entradas no backend.
- Proteção contra acesso indevido a pedidos e dados financeiros.
- Registro de ações críticas.
- Práticas compatíveis com a LGPD.
- Restrição de acesso aos logs de auditoria.
- Controle de sessões e revogação de acessos.

## RNF-004 — Integridade financeira

- Valores monetários armazenados com precisão decimal.
- Pagamentos processados em transações consistentes.
- Proteção contra duplicidade de operações.
- Histórico de pagamentos preservado.
- Fechamentos finalizados imutáveis por operações comuns.
- Correções financeiras realizadas por ajustes auditáveis.
- Separação entre data da venda e data do recebimento.
- Controle de concorrência para impedir alterações simultâneas inconsistentes.

## RNF-005 — Confiabilidade e recuperação

- Tratamento de falhas de conexão.
- Persistência local de rascunhos.
- Recuperação de operações com resultado desconhecido.
- Idempotência de criação de pedidos e pagamentos.
- Controle de versão em entidades sujeitas a concorrência.
- Atualização após reconexão.
- Backups automatizados do banco.
- Monitoramento de falhas.
- Procedimentos de restauração.

## RNF-006 — Escalabilidade

- Suporte a múltiplos estabelecimentos.
- Suporte a múltiplos funcionários simultâneos.
- API preparada para crescimento horizontal, quando necessário.
- Separação modular das regras de negócio.
- Possibilidade de futuras integrações sem reconstrução do domínio.

---

# 5. Arquitetura técnica

## 5.1. Padrão arquitetural

**Monólito modular com separação em camadas.**

Componentes:

1. Aplicativo React Native.
2. API ASP.NET Core.
3. Banco PostgreSQL.
4. SignalR para comunicação em tempo real.
5. Armazenamento de imagens.
6. Serviço de autenticação e autorização.
7. Serviço de auditoria.
8. Persistência local para cache e rascunhos.

## 5.2. Stack sugerida

| Componente | Tecnologia |
|---|---|
| Mobile | React Native + TypeScript |
| Ferramentas mobile | Expo |
| Navegação | Expo Router |
| Requisições e cache | TanStack Query |
| Estado local | Zustand |
| Formulários | React Hook Form + Zod |
| Persistência local | SQLite |
| Backend | C# + ASP.NET Core |
| ORM | Entity Framework Core |
| Banco | PostgreSQL |
| Autenticação | OIDC / OAuth 2.0 com JWT |
| Autorização | Políticas ASP.NET Core |
| Tempo real | SignalR |
| Imagens | Storage compatível com S3 |
| Testes backend | xUnit + testes de integração |
| Testes mobile | Jest + React Native Testing Library |
| Infraestrutura | Docker + hospedagem gerenciada |
| Observabilidade | OpenTelemetry + logs estruturados |
| CI/CD | Azure DevOps Pipelines ou GitHub Actions |

## 5.3. Estrutura do backend

> Substituída pelo ADR-0029.

```text
src/
  Restaurant.Api/
    Controllers/
    Hubs/
    Middlewares/
    Authorization/

  Restaurant.Application/
    Authentication/
    Tenants/
    Employees/
    Products/
    Menus/
    Orders/
    Payments/
    Closing/
    Audit/
    Synchronization/

  Restaurant.Domain/
    Entities/
    Enums/
    Events/
    ValueObjects/
    Services/

  Restaurant.Infrastructure/
    Persistence/
    Repositories/
    Authentication/
    FileStorage/
    Notifications/
    Audit/

tests/
  Restaurant.UnitTests/
  Restaurant.IntegrationTests/
```

## 5.4. Módulos de domínio

**Identity & Tenancy:** usuários, funcionários, estabelecimentos e permissões.

**Catalog:** produtos, pratos, bebidas e preços.

**Menu:** cardápios e disponibilidade.

**Ordering:** comandas, pedidos, itens, pesagens e produção.

**Payment:** pagamentos, recebimentos, estornos e ajustes.

**Closing:** fechamento, conferência de caixa e conciliação.

**Audit:** histórico das operações.

**Notifications:** alertas e comunicação entre dispositivos.

**Synchronization:** recuperação de conexão, controle de versões e idempotência.

---

# 6. Modelo de dados

> Complementado pelos ADRs 0019–0023 e 0026–0034 (veja "Impacto no modelo" em `docs/adr/README.md`).

## 6.1. Entidades principais

| Entidade | Responsabilidade |
|---|---|
| Tenant | Estabelecimento contratante |
| User | Identidade autenticada |
| TenantEmployee | Vínculo usuário-estabelecimento |
| Role | Perfil |
| Permission | Permissão |
| Subscription | Assinatura |
| Product | Prato, bebida ou adicional |
| Menu | Cardápio |
| MenuItem | Item de cardápio |
| Order | Pedido ou comanda |
| OrderItem | Item do pedido |
| WeighingEntry | Pesagem |
| Payment | Pagamento |
| CashMovement | Movimentação do caixa |
| DailyClosing | Fechamento do expediente |
| ClosingAdjustment | Ajuste financeiro posterior |
| OrderChangeEvent | Evento de alteração |
| AlertAcknowledgement | Ciência de alertas |
| AuditLog | Registro de auditoria |

## 6.2. Estrutura conceitual de pedido

```text
Order
  Id
  TenantId
  OrderNumber
  OrderType

  CustomerName
  CustomerPhone
  TableNumber

  Origin
  FulfillmentType
  DeliveryAddress

  ProductionStatus
  IsPaid

  TotalAmount
  BusinessDate

  CreatedBy
  CreatedAt
  UpdatedAt
  Version

  OrderItems[]
  WeighingEntries[]
  Payments[]
  ChangeEvents[]
```

### Tipos de pedido

```text
OrderType
  MARMITA
  PF
  KILO
```

### Estados de produção

```text
ProductionStatus
  OPEN
  IN_PROGRESS
  COMPLETED
```

A situação financeira será derivada dos pagamentos válidos, mantendo `IsPaid` como propriedade consultável.

## 6.3. Estrutura financeira

```text
Payment
  Id
  TenantId
  OrderId
  Amount
  PaymentMethod
  PaidAt
  BusinessDate
  CreatedBy
  IdempotencyKey
  Status

DailyClosing
  Id
  TenantId
  BusinessDate
  ExpectedCash
  CountedCash
  CashDifference
  Justification
  TotalSold
  TotalReceived
  TotalPending
  ClosedBy
  ClosedAt

CashMovement
  Id
  TenantId
  BusinessDate
  MovementType
  Amount
  CreatedBy
  CreatedAt
  Description

ClosingAdjustment
  Id
  TenantId
  RelatedClosingId
  Amount
  Reason
  CreatedBy
  CreatedAt
```

Os fechamentos deverão preservar os valores apurados no momento da finalização.

Recebimentos posteriores serão novas movimentações financeiras, sem sobrescrever o fechamento anterior.

## 6.4. Regras de data e horário

- Armazenar instantes de eventos em UTC.
- Definir a zona horária do estabelecimento.
- Utilizar uma data operacional (`BusinessDate`) para associar vendas e movimentações ao expediente correto.
- Não depender exclusivamente da mudança de data à meia-noite.
- Preservar a data original da venda mesmo após recebimento posterior.
- Manter registros finalizados imutáveis, realizando correções mediante ajustes rastreáveis.

---

# 7. API — Contratos iniciais

## Autenticação

```text
POST   /auth/login
GET    /auth/me

GET    /employees
POST   /employees/invitations
PATCH  /employees/{id}
```

## Produtos e cardápios

```text
GET    /products
POST   /products
PATCH  /products/{id}

GET    /menus
POST   /menus
POST   /menus/{id}/clone
PATCH  /menus/{id}
```

## Pedidos

```text
GET    /orders
POST   /orders
GET    /orders/{id}
PATCH  /orders/{id}

POST   /orders/{id}/items
POST   /orders/{id}/weighings
PATCH  /orders/{id}/status

GET    /orders/{id}/history
POST   /orders/{id}/alerts/acknowledge
```

## Pagamentos

```text
POST   /orders/{id}/payments
GET    /orders/{id}/payments

POST   /payments/{id}/reverse
```

## Fechamento

```text
GET    /closing/summary
GET    /closing/history
POST   /closing/finalize

POST   /closing/cash-movements
GET    /closing/cash-movements

GET    /closing/{id}
POST   /closing/{id}/adjustments
```

## Sincronização

```text
GET    /sync/changes
GET    /operations/{id}
```

### Requisitos técnicos das APIs

- Autorização obrigatória.
- Identificação segura do estabelecimento.
- Validação dos dados de entrada.
- Controle de concorrência.
- Respostas padronizadas.
- Idempotência em operações críticas.
- Paginação quando aplicável.
- Versionamento dos contratos.

---

# 8. Eventos e comunicação em tempo real

O sistema utilizará SignalR para distribuir atualizações entre dispositivos conectados.

### Eventos iniciais

```text
OrderCreated
OrderUpdated
OrderStatusChanged

OrderPaymentConfirmed
OrderPaymentReversed

OrderAlertCreated
OrderAlertAcknowledged

MenuUpdated

ClosingUpdated
ClosingFinalized
```

### Regras

- Os eventos deverão identificar o estabelecimento de destino.
- Funcionários só receberão informações permitidas.
- O banco de dados será a fonte oficial.
- Eventos perdidos deverão ser recuperáveis por consulta à API.
- Reconexões deverão provocar atualização dos dados.
- Atualizações concorrentes deverão utilizar controle de versão.
- Operações críticas deverão ser idempotentes.

A entrega de eventos em tempo real não poderá ser considerada garantia de persistência ou de processamento exatamente uma vez.

---

# 9. Estratégia de sincronização e idempotência

## 9.1. Identificação de operações

Operações críticas, como criação de pedidos e pagamentos, deverão utilizar identificadores únicos de operação.

Exemplo:

```text
POST /orders

Headers:
  Idempotency-Key: <unique-operation-id>
```

O servidor deverá armazenar o resultado de operações idempotentes pelo período configurado e reutilizar o resultado quando receber novamente a mesma solicitação.

## 9.2. Controle de concorrência

Pedidos deverão possuir versão.

Ao atualizar uma comanda, o cliente deverá informar a versão conhecida.

Se outro funcionário já tiver alterado a comanda, a API deverá rejeitar a gravação conflitante e devolver informações suficientes para atualizar a interface.

## 9.3. Recuperação de falhas

Quando uma resposta da API não for recebida, o aplicativo não deverá presumir que a operação falhou.

Deverá:

1. Verificar o identificador da operação.
2. Consultar o resultado no servidor.
3. Atualizar o estado local.
4. Reenviar somente quando seguro.
5. Evitar criar registros duplicados.

## 9.4. Persistência local

O SQLite poderá armazenar:

- Rascunhos.
- Produtos consultados.
- Cardápios em cache.
- Preferências locais.
- Metadados de sincronização.

Dados financeiros pendentes não serão apresentados como confirmados antes do reconhecimento pelo servidor.

---

# 10. Escopo do MVP

## Incluído

- Autenticação B2B2E.
- Gestão de estabelecimentos.
- Funcionários e permissões.
- Quatro abas principais.
- Produtos e bebidas.
- Fotos opcionais.
- Cardápios diários e semanais.
- Reutilização de cardápios.
- Registro de marmitas.
- Registro de PF.
- Registro de refeições por quilo.
- Múltiplos itens por comanda.
- Alteração de pedidos não pagos.
- Registro manual de pagamentos.
- Bloqueio financeiro após quitação.
- Gestão da produção.
- Alertas de alterações.
- Histórico e auditoria.
- Fechamento parcial e definitivo.
- Conciliação de caixa.
- Justificativas para divergências.
- Pagamentos posteriores ao fechamento.
- Histórico financeiro.
- Indicador de conectividade.
- Persistência local de rascunhos.
- Recuperação de conexão.
- Prevenção de operações duplicadas.
- Assinaturas e teste gratuito de 7 dias.

## Fora do MVP

- Chatbot WhatsApp.
- Link de pagamento integrado.
- Confirmação automática de pagamentos.
- Impressoras de comandas.
- Integrações fiscais.
- Controle de estoque.
- Gestão de entregadores.
- Roteirização.
- Programa de fidelidade.
- Dashboard web para restaurantes.
- Operação offline integral.
- IA generativa.

---

# 11. Backlog de features

| ID | Feature | Prioridade |
|---|---|---|
| F-001 | Autenticação e multi-tenancy | P0 |
| F-002 | Funcionários e permissões | P0 |
| F-003 | Cadastro de produtos | P0 |
| F-004 | Cardápios diários e semanais | P0 |
| F-005 | Pedidos de marmitas | P0 |
| F-006 | Pedidos de PF | P0 |
| F-007 | Comandas por quilo | P0 |
| F-008 | Gestão da produção | P0 |
| F-009 | Gestão de pagamentos | P0 |
| F-010 | Fechamento financeiro | P0 |
| F-011 | Conciliação de caixa | P0 |
| F-012 | Pagamentos posteriores ao fechamento | P0 |
| F-013 | Auditoria e histórico | P0 |
| F-014 | Alertas em tempo real | P0 |
| F-015 | Recuperação de conexão | P0 |
| F-016 | Idempotência e concorrência | P0 |
| F-017 | Assinaturas e teste gratuito | P0 |
| F-018 | Chatbot WhatsApp | P1 |
| F-019 | Pagamentos online | P1 |
| F-020 | Impressão de comandas | P2 |
| F-021 | Controle de estoque | P2 |
| F-022 | Offline integral | P2 |

**P0:** obrigatório para o MVP comercial.  
**P1:** evolução planejada.  
**P2:** evolução futura.

---

# 12. Histórias de usuário

### US-001 — Autenticação

Como funcionário, quero acessar o aplicativo com credenciais próprias para utilizar as funcionalidades autorizadas pelo estabelecimento.

### US-002 — Gestão de cardápios

Como gerente, quero cadastrar e reutilizar cardápios para reduzir o tempo de configuração.

### US-003 — Registro de marmitas

Como atendente, quero registrar várias marmitas e bebidas em uma comanda para agilizar o atendimento.

### US-004 — Registro de PF

Como atendente, quero registrar os pratos e bebidas consumidos por uma mesa para manter a conta organizada.

### US-005 — Registro por quilo

Como operador de caixa, quero registrar múltiplas pesagens para calcular corretamente o consumo.

### US-006 — Produção

Como funcionário da cozinha, quero acompanhar os pedidos por status para organizar a preparação.

### US-007 — Alertas

Como funcionário da cozinha, quero ser informado sobre alterações nos pedidos para evitar erros.

### US-008 — Pagamentos

Como caixa, quero registrar pagamentos e bloquear a edição de pedidos quitados para preservar a integridade financeira.

### US-009 — Fechamento

Como proprietário, quero consultar os valores vendidos e recebidos por modalidade para acompanhar o desempenho financeiro.

### US-010 — Conciliação

Como operador de caixa, quero informar o dinheiro contado e comparar com o valor esperado para identificar divergências.

### US-011 — Justificativa de divergências

Como gerente, quero exigir justificativas para diferenças de caixa para manter a rastreabilidade financeira.

### US-012 — Pagamento posterior

Como caixa, quero receber pagamentos referentes a pedidos de dias anteriores sem alterar fechamentos já concluídos.

### US-013 — Auditoria

Como gerente, quero identificar quem realizou alterações em pedidos e pagamentos para investigar inconsistências.

### US-014 — Funcionários

Como proprietário, quero gerenciar funcionários e permissões para controlar o acesso ao sistema.

### US-015 — Recuperação de conexão

Como atendente, quero preservar o rascunho de um pedido durante uma falha de internet para não precisar digitá-lo novamente.

### US-016 — Prevenção de duplicidade

Como atendente, quero que o sistema reconheça pedidos já enviados para evitar comandas duplicadas após uma falha de conexão.

### US-017 — Sincronização

Como funcionário, quero recuperar as alterações feitas por outros usuários depois de uma reconexão para trabalhar com informações atualizadas.

### US-018 — Assinatura

Como proprietário, quero experimentar o aplicativo por sete dias e contratar uma assinatura mensal para continuar utilizando-o.

---

# 13. Critérios gerais de conclusão do épico

O épico estará concluído quando:

1. Estabelecimentos conseguirem cadastrar funcionários e permissões.
2. Os dados entre estabelecimentos estiverem isolados.
3. Pedidos puderem ser registrados nas três modalidades.
4. Cardápios de marmitas e PF funcionarem independentemente.
5. Produtos e bebidas puderem ser adicionados a comandas existentes.
6. Pedidos pagos estiverem protegidos contra edições financeiras.
7. A cozinha conseguir acompanhar a produção em tempo real.
8. Alterações produzirem alertas e histórico.
9. O fechamento apresentar resultados separados e consolidados.
10. O sistema permitir conferência do dinheiro físico.
11. Divergências de caixa exigirem justificativa.
12. Pagamentos posteriores não modificarem fechamentos finalizados.
13. Todas as operações críticas forem auditadas.
14. Rascunhos persistidos localmente sobreviverem a interrupções de conexão.
15. Reenvios de operações não criarem pedidos ou pagamentos duplicados.
16. Alterações concorrentes forem identificadas e tratadas.
17. A sincronização após reconexão funcionar corretamente.
18. O sistema operar adequadamente em aparelhos Android de entrada.
19. Os primeiros estabelecimentos conseguirem utilizar o período gratuito e contratar a assinatura.

---

# 14. Métricas de sucesso

Durante os primeiros testes comerciais deverão ser acompanhados:

- Estabelecimentos cadastrados.
- Estabelecimentos ativos.
- Funcionários ativos.
- Pedidos registrados diariamente.
- Tempo médio de registro de pedidos.
- Quantidade de alterações em comandas.
- Taxa de divergências nos fechamentos.
- Frequência de falhas de sincronização.
- Quantidade de operações duplicadas evitadas.
- Tempo médio de atendimento.
- Conversão dos testes gratuitos em assinaturas.
- Cancelamentos.
- Solicitações de suporte por estabelecimento.

### Meta inicial

Validar o aplicativo em dez estabelecimentos reais, verificando sua capacidade de substituir comandas manuais com precisão operacional e financeira.

---

# 15. Evolução futura — EP-002: Atendimento automatizado

O produto deverá permitir a contratação de um plano avançado com atendimento automatizado pelo WhatsApp.

### Funcionalidades futuras

- Recepção automática do cliente.
- Consulta do cardápio diário através da API.
- Escolha do produto.
- Seleção do tamanho da marmita.
- Escolha dos pratos e acompanhamentos.
- Adição de bebidas e adicionais.
- Entrega ou retirada.
- Identificação do cliente.
- Registro de endereço.
- Escolha da forma de pagamento.
- Geração de links para Pix e cartão.
- Confirmação automática de pagamento.
- Envio automático do pedido para a cozinha.
- Atualizações da situação do pedido.
- Transferência para atendimento humano.

Os pedidos automatizados deverão utilizar o mesmo domínio de pedidos, cardápios e pagamentos do aplicativo.

### Preparação arquitetural

O primeiro épico deverá estabelecer contratos reutilizáveis e permitir identificação da origem de cada pedido.

A automação será implementada posteriormente, sem necessidade de duplicar regras de negócio.

---

# 16. Decisões arquiteturais e de negócio

Veja o índice completo em `docs/adr/README.md` (ADR-0001 a ADR-0042).

**Resultado esperado:** um SaaS mobile capaz de gerenciar pedidos, comandas, produção, recebimentos e fechamento financeiro de pequenos restaurantes, preparado para ampliação comercial e automação do atendimento.
