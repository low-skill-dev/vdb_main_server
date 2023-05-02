﻿using DataAccessLayer.Contexts;
using DataAccessLayer.Models;
using main_server_api.Models.Runtime;
using main_server_api.Models.UserApi.Application.Device;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ServicesLayer.Services;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using vdb_main_server_api.Services;

namespace main_server_api.Controllers;

[Authorize]
[Route("api/[controller]")]
[ApiController]
[Consumes("application/json")]
[Produces("application/json")]
public class ConnectionController : ControllerBase
{
	private readonly VpnContext _context;
	private readonly VpnNodesService _nodesService;
	private readonly VpnNodesStatusService _statusService;
	private readonly ILogger<ConnectionController> _logger;
	public ConnectionController(
		VpnContext context, 
		VpnNodesService nodesService, 
		VpnNodesStatusService statusService, 
		ILogger<ConnectionController> logger)
	{
		_context = context;
		_nodesService = nodesService;
		_statusService = statusService;
		this._logger = logger;
	}

	[HttpGet]
	[AllowAnonymous]
	[Route("nodes-list")]
	public async Task<IActionResult> GetNodesList()
	{
		return await Task.Run(()=>Ok(_statusService.Statuses));
	}

	/* TODO: Создать сервис отложенного отключения.
	 * Пусть этот сервис повторяет запрос на отключение от ноды с частатой,
	 * зависящей от параметра pressure, вычисляемого на основании числа заявок
	 * в очереди. Также что он должен дропать заявки, в случае переподлючения
	 * к ноде. Хотя необходимость дропа сомнительно, ибо если нода недоступна
	 * для отключения, то она недоступна и для подключения... ну, так должно быть.
	 * 
	 * Данный метод имеет некоторую уязвимость безопасности. 
	 * Рассмотрим следующий сценарий:
	 * 
	 * 1. Пользователь отправляет запрос на подключение к ноде с id=1. 
	 *    В базу данных осуществляется запись LastConnectedNodeId=1.
	 * 
	 * 2. Пользователь завершает процесс из диспетчера задач, не произведя
	 *    чистое отключение ('graceful disconnection').
	 *    
	 * 3. Пользователь очищает данные приложения, открывает его заного.
	 * 
	 * 4. Пользователь подключается к ноде id=2. Если в этот момент нода 
	 *    с id=1 недоступна, то строка '_ = _nodesService.RemovePeerFromNode(...'
	 *    это попросту игнорирует. Ложная недоступность возможна даже по причине
	 *    банальной потери пакетов.
	 *   
	 * 5. В случае, если нода с id=1 вновь оказывается доступна до того, как на ней
	 *    будет достигнут интервал обновления и сжатия списка пиров https://github.com/LuminoDiode/rest2wireguard/blob/476021dd1a26e793466e8f711707e66d2f6ed74a/vdb_node_api/Services/PeersBackgroundService.cs#L120
	 *				int delayS = 
	 *					_settings.PeersRenewIntervalSeconds 
	 *					- (int)(DateTime.UtcNow - _lastUpdateUtc).TotalSeconds;
	 *	  то в дейсвительность публичный ключ машины юзверя оказывается добавлен
	 *	  на двух нодах одновременно, что, скопировав приватный ключ на другую машину
	 *	  и при возможности реплицировать данную уязвимость, позволяет имея одну
	 *	  зарегистрированную в базе данных машину, подключить 'её' ко всем имеющимся
	 *	  нодам, а в действительности подключить N машин одновременно, которые будут
	 *	  зарегистрированы в базе данных как одна, а N равно суммарному числу нод 
	 *	  в системе.   
	 */
	[HttpPut]
	public async Task<IActionResult> ConnectToNode([FromBody][Required] ConnectDeviceRequest request)
	{
		int userId = this.ParseIdClaim();

		var foundDevice = _context.Devices.Where(x => x.UserId == this.ParseIdClaim())
			.FirstOrDefault(x => x.WireguardPublicKey == request.WireguardPublicKey);

		if(foundDevice is null) {
			// device does not exist for the user, reset it locally and relogin
			return StatusCode(StatusCodes.Status406NotAcceptable);
		}

		_logger.LogInformation($"Found device with id={foundDevice.Id}. " +
			$"Connecting it to node with id={request.NodeId}...");


		// ensure disconnected from prev node
		if(foundDevice.LastConnectedNodeId is not null
			&& foundDevice.LastConnectedNodeId != request.NodeId) {
			_logger.LogInformation($"Sending disconnection request to the pevious connected " +
				$"node with id={foundDevice.LastConnectedNodeId}...");
			try {
				// not awaited, fire-and-forget
				_ = _nodesService.RemovePeerFromNode(
					foundDevice.WireguardPublicKey, foundDevice.LastConnectedNodeId.Value);
			} catch { }
		}

		foundDevice.LastConnectedNodeId = request.NodeId;
		try {
			_logger.LogInformation($"Sending CONNECTION request for device with ID={foundDevice.Id}" +
				$"to node with ID={foundDevice.LastConnectedNodeId}...");
			var addResult = await _nodesService.AddPeerToNode(foundDevice.WireguardPublicKey, request.NodeId);
			if(addResult is not null && addResult.InterfacePublicKey is not null) {
				var node = _nodesService.NameToNode[_nodesService.GetNodeNameById(request.NodeId)].nodeInfo;
				await _context.SaveChangesAsync();
				return Ok(new ConnectDeviceResponse(addResult, 
					request.WireguardPublicKey, node.IpAddress.ToString(), node.WireguardPort));
			} else {
				_logger.LogInformation($"Unable to add pubkey \'{request.WireguardPublicKey.Substring(0, 3)}...\' " +
				$"to node {request.NodeId}.");
				return Problem(Utf8Json.JsonSerializer.ToJsonString(addResult));
			}
		} catch(Exception ex) {
			try {
				// not awaited, fire-and-forget
				_ = _nodesService.RemovePeerFromNode( // LastConnectedNodeId is not null here!
					foundDevice.WireguardPublicKey, foundDevice.LastConnectedNodeId.Value);
			} catch { }
			_logger.LogInformation($"Unable to add pubkey \'{request.WireguardPublicKey.Substring(0, 3)}...\' " +
				$"to node {request.NodeId}: \'{ex.Message}\'.");
		}

		return StatusCode(StatusCodes.Status500InternalServerError);
	}
}
