using System.Net;
using System.Net.Http;
using System.Net.Http.Json;

namespace WpfApp2;

public sealed class ChartServiceClient {
  private static readonly HttpClient defaultClient = new() {
    Timeout = TimeSpan.FromSeconds(20)
  };
  private readonly HttpClient httpClient;

  public ChartServiceClient(HttpClient? client = null) {
    httpClient = client ?? defaultClient;
  }

  public async Task<byte[]> renderAsync(SearchInput input, PlotViewport view,
      int width, int height, CancellationToken cancellationToken, double lineWidth = 2) {
    var chart = await Task.Run(() => buildChart(input, view, lineWidth, cancellationToken),
        cancellationToken);
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(20));
    var requestToken = timeout.Token;
    using var request = new HttpRequestMessage(HttpMethod.Post, "https://quickchart.io/chart") {
      Content = JsonContent.Create(new {
        version = "4",
        format = "svg",
        width = Math.Clamp(width, 300, 3000),
        height = Math.Clamp(height, 300, 3000),
        devicePixelRatio = 1,
        backgroundColor = "transparent",
        chart
      })
    };
    HttpResponseMessage response;
    try {
      response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
          requestToken);
    } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
      throw new ServiceException("Сервис графиков не ответил.");
    } catch (HttpRequestException) {
      throw new ServiceException("Нет связи с сервисом графиков.");
    }

    using (response) {
      if (response.StatusCode == HttpStatusCode.TooManyRequests) {
        TimeSpan retry = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)
            ?? TimeSpan.FromMinutes(1);
        throw new ServiceException("Сервис временно ограничил запросы. График обновится позже.") {
          retryAfter = retry > TimeSpan.Zero ? retry : TimeSpan.FromSeconds(1)
        };
      }
      if (!response.IsSuccessStatusCode
          || response.Headers.Contains("X-quickchart-error")
          || response.Content.Headers.ContentType?.MediaType != "image/svg+xml") {
        throw new ServiceException("Сервис не построил график.");
      }
      try {
        using var stream = await response.Content.ReadAsStreamAsync(requestToken);
        using var output = new System.IO.MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(buffer, requestToken)) > 0) {
          if (output.Length + read > 8_000_000) {
            throw new ServiceException("Слишком большой ответ сервиса графиков.");
          }
          output.Write(buffer, 0, read);
        }
        return output.ToArray();
      } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
        throw new ServiceException("Сервис графиков не ответил.");
      } catch (HttpRequestException) {
        throw new ServiceException("Не удалось загрузить график.");
      } catch (System.IO.IOException) {
        throw new ServiceException("Не удалось загрузить график.");
      }
    }
  }

  private static object buildChart(SearchInput input, PlotViewport view,
      double lineWidth, CancellationToken token) {
    var datasets = new List<object>();
    double cutoff = Math.Max(Math.Abs(view.bottom), Math.Abs(view.top));
    cutoff = cutoff < double.MaxValue / 8 ? Math.Max(cutoff * 8, 1) : double.MaxValue;
    for (int curve = 0; curve < (input.mode == SearchMode.ROOT ? 1 : 2); curve++) {
      var points = new List<object>();
      double previousX = view.left;
      for (int i = 0; i <= 2048; i++) {
        token.ThrowIfCancellationRequested();
        double x = i == 2048 ? view.right : view.left + (view.right - view.left) * i / 2048;
        var d = input.formula.derivatives(x);
        double y = curve == 0 ? d.value : d.first;
        bool continuous = i == 0 || input.formula.enclose(previousX, x).valid;
        if (!continuous) {
          points.Add(new {
            x,
            y = (double?)null
          });
        }
        points.Add(new {
          x,
          y = double.IsFinite(y) && Math.Abs(y) <= cutoff ? (double?)y : null
        });
        previousX = x;
      }
      datasets.Add(new {
        data = points,
        showLine = true,
        fill = false,
        pointRadius = 0,
        borderColor = curve == 0 ? "#2563EB" : "#8B5CF6",
        borderWidth = lineWidth * (curve == 0 ? 1 : 1.25),
        borderDash = curve == 0 ? Array.Empty<int>() : new[] { 6, 4 },
        spanGaps = false,
        tension = 0
      });
    }
    return new {
      type = "scatter",
      data = new {
        datasets
      },
      options = new {
        responsive = false,
        animation = false,
        parsing = false,
        layout = new {
          padding = 0,
          autoPadding = false
        },
        plugins = new {
          legend = new {
            display = false
          },
          tooltip = new {
            enabled = false
          }
        },
        scales = new {
          x = new {
            type = "linear",
            display = false,
            min = view.left,
            max = view.right
          },
          y = new {
            type = "linear",
            display = false,
            min = view.bottom,
            max = view.top
          }
        }
      }
    };
  }
}
