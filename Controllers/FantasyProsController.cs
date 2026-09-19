using FantasyProsScrape.Jobs;
using Microsoft.AspNetCore.Mvc;
using Quartz;

namespace FantasyProsScrape.Controllers;

[ApiController]
[Route("api/fantasypros")]
public class FantasyProsController : ControllerBase
{
    private readonly ISchedulerFactory _schedulerFactory;
    private readonly ILogger<FantasyProsController> _logger;

    public FantasyProsController(ISchedulerFactory schedulerFactory, ILogger<FantasyProsController> logger)
    {
        _schedulerFactory = schedulerFactory;
        _logger = logger;
    }

    /// <summary>
    /// Triggers <see cref="FantasyProsRanksJob"/> now, optionally overriding season/week/positions
    /// (positions as a comma-separated list, e.g. "RB,WR"). Returns 202: the job runs asynchronously
    /// on Quartz's thread pool and its summary lands in the logs, not in this response.
    /// </summary>
    [HttpPost("run")]
    public async Task<IActionResult> Run(
        [FromQuery] int? season = null,
        [FromQuery] int? week = null,
        [FromQuery] string? positions = null)
    {
        var scheduler = await _schedulerFactory.GetScheduler();

        var dataMap = new JobDataMap();
        if (season.HasValue)
            dataMap.Put(FantasyProsRanksJob.SeasonDataKey, season.Value);
        if (week.HasValue)
            dataMap.Put(FantasyProsRanksJob.WeekDataKey, week.Value);
        if (!string.IsNullOrWhiteSpace(positions))
            dataMap.Put(FantasyProsRanksJob.PositionsDataKey, positions);

        await scheduler.TriggerJob(new JobKey(FantasyProsRanksJob.JobName), dataMap);

        _logger.LogInformation(
            "Manually triggered {Job} (season={Season}, week={Week}, positions={Positions})",
            FantasyProsRanksJob.JobName, season, week, positions ?? "<all>");

        return Accepted(new
        {
            message = $"Triggered {FantasyProsRanksJob.JobName}. This is the dry-run job (FEAT-7): it resolves and logs the diff, it writes nothing. Watch the logs for the per-position summary.",
            season,
            week,
            positions
        });
    }
}
