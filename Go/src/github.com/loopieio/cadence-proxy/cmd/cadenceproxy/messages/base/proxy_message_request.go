package base

import "log"

type (

	// ProxyRequest "extends" ProxyMessage and it is
	// a type of ProxyMessage that comes into the server
	// i.e. a request
	//
	// A ProxyRequest contains a RequestId and a reference to a
	// ProxyMessage struct
	ProxyRequest struct {

		// ProxyMessage is a reference to a ProxyMessage in memory
		*ProxyMessage

		// RequestId is the unique id of the ProxyRequest
		RequestId int64
	}
)

// GetRequestID gets a request id from a ProxyMessage's properties
func (request *ProxyRequest) GetRequestID(key string) int64 {
	return request.ProxyMessage.GetLongProperty(RequestIDKey)
}

// SetRequestID sets a request id in a ProxyRequest's ProxyMessage
// properties
func (request *ProxyRequest) SetRequestID(value int64) {
	request.ProxyMessage.SetLongProperty(RequestIDKey, value)
}

// Clone inherits docs from ProxyMessage.Clone()
func (request *ProxyRequest) Clone() IProxyMessage {
	proxyRequest := ProxyRequest{
		ProxyMessage: new(ProxyMessage),
	}

	var messageClone IProxyMessage = &proxyRequest
	request.CopyTo(messageClone)

	return messageClone
}

// CopyTo inherits docs from ProxyMessage.CopyTo()
func (request *ProxyRequest) CopyTo(target IProxyMessage) {
	request.ProxyMessage.CopyTo(target)
	v, ok := target.(*ProxyRequest)
	if ok {
		v.RequestId = request.RequestId
		*v.ProxyMessage = *request.ProxyMessage
	}
}

// String inherits docs from ProxyMessage.String()
func (request *ProxyRequest) String() {
	log.Print("{\n")
	log.Println()
	log.Printf("\tRequestId: %d\n", request.RequestId)
	request.ProxyMessage.String()
	log.Print("}\n\n")
}
