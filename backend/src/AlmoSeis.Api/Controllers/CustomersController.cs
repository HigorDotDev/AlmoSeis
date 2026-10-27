using AlmoSeis.Api.Infrastructure;
using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Security;
using Microsoft.AspNetCore.Mvc;

namespace AlmoSeis.Api.Controllers;

/// Clientes e fiado (06 §6.8).
[ApiController]
[Route("api/v1/customers")]
public sealed class CustomersController : ControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.Orders.View)]
    public async Task<ActionResult<PageDto<CustomerDto>>> Search(
        [FromQuery] string? search, [FromQuery] string? cursor,
        [FromServices] IQueryHandler<SearchCustomersQuery, PageDto<CustomerDto>> handler,
        CancellationToken cancellationToken, [FromQuery] int limit = 20) =>
        Ok(await handler.HandleAsync(new SearchCustomersQuery(search, limit, cursor), cancellationToken));

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.Orders.View)]
    public async Task<ActionResult<CustomerDto>> Get(
        Guid id, [FromServices] IQueryHandler<GetCustomerQuery, CustomerDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new GetCustomerQuery(id), cancellationToken));

    [HttpPost]
    [HasPermission(Permissions.CustomersManage)]
    public async Task<ActionResult<CustomerDto>> Create(
        [FromBody] CreateCustomerCommand command,
        [FromServices] ICommandHandler<CreateCustomerCommand, CustomerDto> handler, CancellationToken cancellationToken)
    {
        var customer = await handler.HandleAsync(command, cancellationToken);
        return Created($"/api/v1/customers/{customer.Id}", customer);
    }

    [HttpPatch("{id:guid}")]
    [HasPermission(Permissions.CustomersManage)]
    [RequiresIfMatch]
    public async Task<ActionResult<CustomerDto>> Update(
        Guid id, [FromBody] UpdateCustomerRequest request,
        [FromServices] ICommandHandler<UpdateCustomerCommand, CustomerDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new UpdateCustomerCommand(id, request), cancellationToken));

    [HttpPost("{id:guid}/addresses")]
    [HasPermission(Permissions.CustomersManage)]
    [RequiresIfMatch]
    public async Task<ActionResult<CustomerDto>> AddAddress(
        Guid id, [FromBody] CustomerAddressInput address,
        [FromServices] ICommandHandler<AddCustomerAddressCommand, CustomerDto> handler, CancellationToken cancellationToken)
    {
        var customer = await handler.HandleAsync(new AddCustomerAddressCommand(id, address), cancellationToken);
        return Created($"/api/v1/customers/{customer.Id}", customer);
    }

    [HttpPatch("{id:guid}/addresses/{addressId:guid}")]
    [HasPermission(Permissions.CustomersManage)]
    [RequiresIfMatch]
    public async Task<ActionResult<CustomerDto>> UpdateAddress(
        Guid id, Guid addressId, [FromBody] UpdateCustomerAddressRequest request,
        [FromServices] ICommandHandler<UpdateCustomerAddressCommand, CustomerDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new UpdateCustomerAddressCommand(id, addressId, request), cancellationToken));

    [HttpDelete("{id:guid}/addresses/{addressId:guid}")]
    [HasPermission(Permissions.CustomersManage)]
    [RequiresIfMatch]
    public async Task<ActionResult<CustomerDto>> RemoveAddress(
        Guid id, Guid addressId,
        [FromServices] ICommandHandler<RemoveCustomerAddressCommand, CustomerDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new RemoveCustomerAddressCommand(id, addressId), cancellationToken));

    [HttpPost("{id:guid}/anonymize")]
    [HasPermission(Permissions.SettingsManage)]
    [RequiresIfMatch]
    public async Task<ActionResult<CustomerDto>> Anonymize(
        Guid id, [FromBody] AnonymizeCustomerRequest request,
        [FromServices] ICommandHandler<AnonymizeCustomerCommand, CustomerDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new AnonymizeCustomerCommand(id, request), cancellationToken));

    [HttpGet("{id:guid}/pending-orders")]
    [HasPermission(Permissions.Orders.View)]
    public async Task<ActionResult<CustomerPendingOrdersDto>> PendingOrders(
        Guid id, [FromServices] IQueryHandler<GetCustomerPendingOrdersQuery, CustomerPendingOrdersDto> handler, CancellationToken cancellationToken) =>
        Ok(await handler.HandleAsync(new GetCustomerPendingOrdersQuery(id), cancellationToken));
}
