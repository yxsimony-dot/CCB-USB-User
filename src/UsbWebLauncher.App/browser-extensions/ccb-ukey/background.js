//Copyright 2022 CCB
var ccb_port = "ccb.detector.ukey.edgeextension";
var ccb_web = null;
var ccb_native = null;
var ccb_action = null;

var actCheckCCBExtension = "CheckCCBExtension";
chrome.runtime.onMessageExternal.addListener(function(request, sender,sendResponse) {
	if(request.CCB == undefined)
	{
		return false;
	}
	if(request.CCB != actCheckCCBExtension)
	{
		return false;
	}
		
	sendResponse({"Action":request.CCB,"responseData":0});
	return true;
});  

function connectCCBNative()
{
	if(ccb_native != null){
		return;
	}
	ccb_native = chrome.runtime.connectNative(ccb_port);
	ccb_native.onMessage.addListener(function(curMsg) {
		ccb_web.postMessage(curMsg);
		ccb_action = null;
	});
	ccb_native.onDisconnect.addListener(function() {
		ccb_native = null;
		if(ccb_action != null){
			ccb_web.postMessage({"Action":ccb_action,"responseData":-1021});
			ccb_action = null;
		}
	});
}

function ccbSendMsg(curMsg)
{
	ccb_action = curMsg.CCB;
	if(ccb_native == null){
		connectCCBNative();
	}
	
	try{
        ccb_native.postMessage(curMsg);
    }catch(e){
		ccb_web.postMessage({"Action":curMsg.CCB,"responseData":-1021});
		ccb_action = null;
    }
}


chrome.runtime.onConnectExternal.addListener(function(ccbCurWeb) {
	if(ccb_web != null && ccb_web != ccbCurWeb){
		ccb_web.disconnect();
	}
	ccb_web = ccbCurWeb;
	connectCCBNative();
	ccb_web.onMessage.addListener(function(curMsg) {
		if(curMsg.CCB != undefined){
			ccbSendMsg(curMsg);
		}
	});
	ccb_web.onDisconnect.addListener(function() {
	});
});

