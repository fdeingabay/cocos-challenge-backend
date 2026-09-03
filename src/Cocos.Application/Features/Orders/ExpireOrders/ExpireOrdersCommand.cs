namespace Cocos.Application.Features.Orders.ExpireOrders;

/// <summary>Vence las órdenes LIMIT vivas cuya jornada ya termino.</summary>
public sealed record ExpireOrdersCommand;

public sealed record ExpireOrdersResponse(int ExpiredCount);
