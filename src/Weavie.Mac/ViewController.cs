using ObjCRuntime;
using WebKit;
using Foundation;
using AppKit;
using CoreGraphics;

namespace Weavie.Mac;

// QUICK LATENCY SPIKE (R15): real Monaco in the real WKWebView so the user can
// type and judge feel before committing to the overnight build. Loads Monaco
// from CDN, shows a rough keydown->next-frame latency readout. Window stays
// open; JS reports load status / errors to stdout via the bridge.
public partial class ViewController : NSViewController, IWKScriptMessageHandler {
	WKWebView? _webView;

	protected ViewController (NativeHandle handle) : base (handle)
	{
	}

	public override void ViewDidLoad ()
	{
		base.ViewDidLoad ();

		var config = new WKWebViewConfiguration ();
		config.UserContentController.AddScriptMessageHandler (this, "weavie");

		_webView = new WKWebView (View.Bounds, config) {
			AutoresizingMask = NSViewResizingMask.WidthSizable | NSViewResizingMask.HeightSizable,
		};
		View.AddSubview (_webView);
		_webView.LoadHtmlString (Html, new NSUrl ("https://cdn.jsdelivr.net/"));
		Console.WriteLine ("[spike] loading Monaco in WKWebView...");
	}

	public override void ViewDidAppear ()
	{
		base.ViewDidAppear ();
		var w = View.Window;
		if (w != null) {
			w.SetContentSize (new CGSize (1100, 760));
			w.Center ();
			w.Title = "weavie — Monaco latency spike (type here)";
			w.MakeKeyAndOrderFront (null);
		}
	}

	[Export ("userContentController:didReceiveScriptMessage:")]
	public void DidReceiveScriptMessage (WKUserContentController userContentController, WKScriptMessage message)
	{
		Console.WriteLine ($"[spike] {message.Body}");
	}

	public override NSObject RepresentedObject {
		get => base.RepresentedObject;
		set => base.RepresentedObject = value;
	}

	const string Html = """
	<!doctype html><html><head><meta charset="utf-8">
	<style>
	  html,body{margin:0;height:100%;background:#1e1e1e;color:#ddd;font-family:-apple-system}
	  #ed{position:absolute;inset:0;top:34px}
	  #bar{position:fixed;left:0;right:0;top:0;height:34px;display:flex;align-items:center;
	       gap:18px;padding:0 12px;background:#252526;border-bottom:1px solid #333;
	       font:12px ui-monospace,Menlo,monospace;z-index:10}
	  .n{color:#4ec9b0}
	</style></head>
	<body>
	  <div id="bar">
	    <span>keydown→frame: p50 <span class="n" id="p50">–</span> ms ·
	      p95 <span class="n" id="p95">–</span> ms · max <span class="n" id="mx">–</span> ms</span>
	    <span id="st">loading…</span>
	  </div>
	  <div id="ed"></div>
	<script>
	  const post=m=>{try{window.webkit.messageHandlers.weavie.postMessage(m)}catch(e){}};
	  window.onerror=(m,s,l,c)=>post("JS ERROR: "+m+" @"+l+":"+c);
	  const base="https://cdn.jsdelivr.net/npm/monaco-editor@0.52.2/min/";
	  self.MonacoEnvironment={getWorkerUrl:()=>"data:text/javascript;charset=utf-8,"+
	    encodeURIComponent("self.MonacoEnvironment={baseUrl:'"+base+"'};importScripts('"+base+"vs/base/worker/workerMain.js');")};

	  // rough input-latency meter: keydown timestamp -> next animation frame
	  const buf=[];
	  addEventListener("keydown",e=>{const t0=e.timeStamp;requestAnimationFrame(t=>{
	    buf.push(t-t0);if(buf.length>120)buf.shift();
	    const s=[...buf].sort((a,b)=>a-b);const q=p=>s[Math.min(s.length-1,Math.floor(p*s.length))]||0;
	    p50.textContent=q(.5).toFixed(1);p95.textContent=q(.95).toFixed(1);
	    mx.textContent=Math.max(...buf).toFixed(1);
	  })},true);

	  const ld=document.createElement("script");ld.src=base+"vs/loader.js";
	  ld.onload=()=>{require.config({paths:{vs:base+"vs"}});require(["vs/editor/editor.main"],()=>{
	    monaco.editor.create(document.getElementById("ed"),{
	      value:["// weavie latency spike — type freely; watch the readout up top.",
	             "// This is real Monaco in real WKWebView on your display.",
	             "",
	             "function fib(n){",
	             "  if (n < 2) return n;",
	             "  return fib(n-1) + fib(n-2);",
	             "}",
	             "",
	             "const xs = Array.from({length: 20}, (_, i) => fib(i));",
	             "console.log(xs);",""].join("\n"),
	      language:"javascript",theme:"vs-dark",fontSize:14,
	      bracketPairColorization:{enabled:true},minimap:{enabled:true},automaticLayout:true
	    });
	    st.textContent="monaco ready";post("monaco-ready");
	  })};
	  ld.onerror=()=>{st.textContent="LOADER FAILED";post("loader.js FAILED to load (network/CDN?)")};
	  document.head.appendChild(ld);
	</script></body></html>
	""";
}
