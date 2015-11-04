(*
 * Copyright 2015 INRIA and Microsoft Corporation
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * 
 *     http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *)

#light "off"

/// <summary>
/// Module receiving, sending and forwarding and eventually encrypts TLS records.
/// </summary>
module FlexTLS.FlexRecord

open NLog

open Tcp
open Bytes
open Error
open TLSInfo
open TLSConstants

open FlexTypes
open FlexConstants
open FlexState

/// <summary>
/// Get fragment size depending on the fragmentation policy
/// </summary>
/// <param name="fp"> Fragmentation policy </param>
/// <returns> size of the fragment to be applied </returns>
let fs_of_fp (fp:fragmentationPolicy) : int =
    match fp with
    | All(n) | One(n) -> n

/// <summary>
/// Split any payload depending on the fragmentation size
/// </summary>
/// <param name="bytes"> Data bytes to be split </param>
/// <param name="fp"> Fragmentation policy </param>
/// <returns> Fragment of the chosen size * remaining unsplit data bytes </returns>
let splitPayloadFP (b:bytes) (fp:fragmentationPolicy) : bytes * bytes =
    let len = System.Math.Min((length b),(fs_of_fp fp)) in
    Bytes.split b len

/// <summary>
/// Select a buffer to use depending on the content type
/// </summary>
/// <param name="channel"> Channel to extract buffer from </param>
/// <param name="ct"> Content type </param>
/// <returns> Buffer associated to the chosen content type </returns>

let pickCTBuffer (ch:channel) (ct:ContentType) : bytes =
    match ct with
    | Handshake         -> ch.hs_buffer
    | Alert             -> ch.alert_buffer
    | Application_data  -> ch.appdata_buffer
    | _ -> failwith "Unsupported content type"

/// <summary>
/// Module receiving, sending and forwarding and eventually encrypting TLS records.
/// </summary>
type FlexRecord =
    class

    /// <summary>
    /// Read a record fragment header to get ContentType, ProtocolVersion and Length of the fragment
    /// </summary>
    /// <param name="st"> State of the current Handshake </param>
    /// <returns> ContentType * ProtocolVersion * Length * Header bytes </returns>
    static member parseFragmentHeader (st:state) : ContentType * ProtocolVersion * nat * bytes =
        match Tcp.read st.ns 5 with
        | Error x -> failwith (perror __SOURCE_FILE__ __LINE__ x)
        | Correct header ->
            match Record.parseHeader header with
            | Error (ad,x) -> failwith (perror __SOURCE_FILE__ __LINE__ x)
            | Correct(ct,pv,len) -> ct,pv,len,header

    /// <summary>
    /// Get N bytes from the network stream and decrypts it as a fragment
    /// </summary>
    /// <param name="st"> State of the current Handshake </param>
    /// <param name="ct"> Content type of the fragment </param>
    /// <param name="len"> Length of the fragment </param>
    /// <returns> Updated state (reading side) * decrypted plaintext </returns>
    static member getFragmentContent (st:state, ct:ContentType, len:int) : state * bytes =
        match Tcp.read st.ns len with
        | Error x -> failwith (perror __SOURCE_FILE__ __LINE__ x)
        | Correct payload ->
            match Record.recordPacketIn st.read.epoch st.read.record ct payload with
            | Error (ad,x) -> failwith (perror __SOURCE_FILE__ __LINE__ x)
            | Correct (rec_in,rg,frag)  ->
                let st = FlexState.updateIncomingRecord st rec_in in
                let id = TLSInfo.mk_id st.read.epoch in
                let fragb = TLSFragment.reprFragment id ct rg frag in
                let st = FlexState.updateLog st ct fragb in
                LogManager.GetLogger("file").Trace(sprintf "+++ Record : %s" (Bytes.hexString(fragb)));
                (st,fragb)

    /// <summary>
    /// Receive a fragment by reading a header and a payload from the network stream
    /// </summary>
    /// <param name="st"> State of the current Handshake </param>
    /// <returns> State * ContentType * ProtocolVersion * Lenght of the record payload * Fragment header bytes * Fragment payload bytes </returns>

    static member receive (st:state) : state * ContentType * ProtocolVersion * int * bytes * bytes =
        let ct,pv,len,header = FlexRecord.parseFragmentHeader st in
        let st,payload = FlexRecord.getFragmentContent (st,ct,len) in
        st,ct,pv,len,header,payload

    /// <summary>
    /// Encrypt data depending on the record connection state
    /// </summary>
    /// <param name="e"> Epoch to use for encryption </param>
    /// <param name="pv"> Protocol version to use </param>
    /// <param name="k"> Record connection state to use </param>
    /// <param name="ct"> Content type of the fragment </param>
    /// <param name="payload"> Data to encrypt </param>
    /// <returns> Updated incoming record state * ciphertext </returns>
    static member encrypt (e:epoch, pv:ProtocolVersion, k:Record.ConnectionState, ct:ContentType, payload:bytes) : Record.ConnectionState * bytes =
        // pv is the protocol version set in the record header.
        // For encrypting epochs, it'd better match the protocol version contained in the epoch, since the latter is used for the additional data
        let len = length payload in
        let rg : Range.range = (len,len) in
        let id = TLSInfo.mk_id e in
        let frag = TLSFragment.fragment id ct rg payload in
        let k,b = Record.recordPacketOut e k pv rg ct frag in
        (k,b)

    /// <summary>
    /// Forward a record
    /// </summary>
    /// <param name="stin"> State of the current Handshake on the incoming side </param>
    /// <param name="stout"> State of the current Handshake on the outgoing side </param>
    /// <param name="fp"> Optional fragmentation policy applied to the message </param>
    /// <returns> Updated incoming state * Updated outgoing state * forwarded record bytes </returns>
    static member forward (stin:state, stout:state, ?fp:fragmentationPolicy) : state * state * bytes =
        let fp = defaultArg fp FlexConstants.defaultFragmentationPolicy in

        let ct,pv,len,header = FlexRecord.parseFragmentHeader(stin) in
        let stin,payload = FlexRecord.getFragmentContent(stin,ct,len) in
        let stout = FlexState.updateOutgoingBuffer stout ct payload in
        let stout = FlexRecord.send(stout,ct,fp) in
        stin,stout,payload

    /// <summary>
    /// Send data picked from a chosen CT buffer over the network after encrypting it and according to the fragmentation policy
    /// </summary>
    /// <param name="st"> State of the current Handshake </param>
    /// <param name="ct"> Content type of the fragment </param>
    /// <param name="fp"> Optional fragmentation policy applied to the message </param>
    /// <returns> Updated outgoing record state </returns>
    /// <remarks> We leave the remainder in the buffer </remarks>
    static member send (st:state, ct:ContentType, ?fp:fragmentationPolicy) : state =
        let fp = defaultArg fp FlexConstants.defaultFragmentationPolicy in
        let payload = pickCTBuffer st.write ct in
        let k,b,rem = FlexRecord.send(st.ns,st.write.epoch,st.write.record,ct,payload,st.write.epoch_init_pv,fp) in
        let st = FlexState.updateLog st ct b in
        let st = FlexState.updateOutgoingBuffer st ct rem in
        let st = FlexState.updateOutgoingRecord st k in
        st

    /// <summary>
    /// Send data over the network after encrypting a record depending on the fragmentation policy
    /// </summary>
    /// <param name="ns"> Network stream </param>
    /// <param name="e"> Epoch to use for encryption </param>
    /// <param name="k"> Record connection state to use </param>
    /// <param name="ct"> Content type of the fragment </param>
    /// <param name="payload"> Data to encrypt </param>
    /// <param name="epoch_init_pv"> Optional Protocol version set for the Initial epoch </param>
    /// <param name="fp"> Optional fragmentation policy applied to the message </param>
    /// <returns> Updated outgoing record state * remainder of the plain data </returns>
    static member send (ns:NetworkStream, e:epoch, k:Record.ConnectionState, ct:ContentType, payload:bytes, ?epoch_init_pv:ProtocolVersion, ?fp:fragmentationPolicy) : Record.ConnectionState * bytes * bytes =
        let fp = defaultArg fp FlexConstants.defaultFragmentationPolicy in
        let pv =
            if TLSInfo.isInitEpoch e then
                match epoch_init_pv with
                | None -> failwith (perror __SOURCE_FILE__ __LINE__ "A protocol version value must be provided for the initial epoch")
                | Some(pv) -> pv
            else
                let si = TLSInfo.epochSI e in
                si.protocol_version
        in
        let msgb,rem = splitPayloadFP payload fp in
        LogManager.GetLogger("file").Trace(sprintf "+++ Record : %s" (Bytes.hexString(msgb)));
        let k,b = FlexRecord.encrypt (e,pv,k,ct,msgb) in
        match Tcp.write ns b with
        | Error x -> failwith x
        | Correct() ->
            match fp with
            | All(fs) ->
                if rem = empty_bytes then
                    (k,msgb,rem)
                else
                    let k,b,rem = FlexRecord.send(ns,e,k,ct,rem,pv,fp) in
                    (k,msgb@|b,rem)
            | One(fs) -> (k,msgb,rem)

    /// <summary>
    /// Encapsulate the given payload with a record header; does not perform encryption.
    /// </summary>
    /// <param name="ns"> Network stream </param>
    /// <param name="ct"> Content type of the fragment </param>
    /// <param name="pv"> Protocol Version of the fragment </param>
    /// <param name="payload"> Data to encrypt </param>
    /// <param name="fp"> Optional fragmentation policy applied to the message </param>
    /// <returns> Remaining bytes </returns>
    static member send_raw (ns:NetworkStream, ct:ContentType, pv:ProtocolVersion, payload:bytes, ?fp:fragmentationPolicy) : bytes =
        let fp = defaultArg fp FlexConstants.defaultFragmentationPolicy in
        let b,rem = splitPayloadFP payload fp in
        let fragb = Record.makePacket ct pv b in
        LogManager.GetLogger("file").Trace(sprintf "+++ Record : %s" (Bytes.hexString(fragb)));
        match Tcp.write ns fragb with
        | Error x -> failwith x
        | Correct() ->
            match fp with
            | All(fs) ->
                if rem = empty_bytes then
                    empty_bytes
                else
                    FlexRecord.send_raw(ns,ct,pv,rem,fp)
            | One(fs) -> rem

    end
